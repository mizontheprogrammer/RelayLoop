using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using RelayLoop.App.Native;

namespace RelayLoop.App.Services;

public readonly record struct HotKeyGesture(HotKeyModifiers Modifiers, uint VirtualKey)
{
    public static HotKeyGesture RecordDefault { get; } =
        new(HotKeyModifiers.Control | HotKeyModifiers.Shift | HotKeyModifiers.Alt | HotKeyModifiers.NoRepeat, 0x52);

    public static HotKeyGesture PlayDefault { get; } =
        new(HotKeyModifiers.Control | HotKeyModifiers.Shift | HotKeyModifiers.Alt | HotKeyModifiers.NoRepeat, 0x50);

    public static HotKeyGesture EmergencyStopDefault { get; } =
        new(HotKeyModifiers.Control | HotKeyModifiers.Shift | HotKeyModifiers.Alt | HotKeyModifiers.NoRepeat, 0x53);

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotKeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotKeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotKeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotKeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(FormatVirtualKey(VirtualKey));
        return string.Join('+', parts);
    }

    private static string FormatVirtualKey(uint virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x70 + 1}";
        }

        return virtualKey switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x24 => "Home",
            0x23 => "End",
            0x2D => "Insert",
            0x2E => "Delete",
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            _ => $"VK 0x{virtualKey:X2}",
        };
    }
}

public sealed class HotKeyPressedEventArgs(
    int registrationId,
    string name,
    HotKeyGesture gesture) : EventArgs
{
    public int RegistrationId { get; } = registrationId;

    public string Name { get; } = name;

    public HotKeyGesture Gesture { get; } = gesture;
}

public sealed class HotKeyRegistrationException : Win32Exception
{
    public const int HotKeyAlreadyRegisteredError = 1409;

    public HotKeyRegistrationException(string name, HotKeyGesture gesture, int errorCode)
        : base(errorCode, CreateMessage(name, gesture, errorCode))
    {
        HotKeyName = name;
        Gesture = gesture;
        IsConflict = errorCode == HotKeyAlreadyRegisteredError;
    }

    public string HotKeyName { get; }

    public HotKeyGesture Gesture { get; }

    public bool IsConflict { get; }

    private static string CreateMessage(string name, HotKeyGesture gesture, int errorCode) =>
        errorCode == HotKeyAlreadyRegisteredError
            ? $"The {name} hotkey ({gesture}) is already in use by this or another application. Choose a different shortcut."
            : $"Windows could not register the {name} hotkey ({gesture}); error {errorCode}.";
}

public interface IGlobalHotKeyRegistration : IDisposable
{
    int Id { get; }

    string Name { get; }

    HotKeyGesture Gesture { get; }
}

public interface IGlobalHotKeyService : IDisposable
{
    event EventHandler<HotKeyPressedEventArgs>? HotKeyPressed;

    IGlobalHotKeyRegistration Register(string name, HotKeyGesture gesture);
}

/// <summary>
/// Registers thread-scoped Win32 hotkeys and owns the required message queue. All register and
/// unregister calls execute on the queue thread, including cleanup during disposal.
/// </summary>
public sealed class GlobalHotKeyService : IGlobalHotKeyService
{
    private const uint PeekNoRemove = 0;
    private readonly IHotKeyNativeFacade _native;
    private readonly ConcurrentQueue<IPendingCommand> _commands = new();
    private readonly Dictionary<int, Registration> _registrations = [];
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Thread _thread;
    private uint _threadId;
    private int _nextRegistrationId;
    private Exception? _startupException;
    private bool _disposed;

    public GlobalHotKeyService(IHotKeyNativeFacade? native = null)
    {
        _native = native ?? new WindowsHotKeyApi();
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "RelayLoop global hotkeys",
        };
        _thread.Start();

        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The global-hotkey message thread did not initialize in time.");
        }

        if (_startupException is not null)
        {
            throw new InvalidOperationException("The global-hotkey message thread failed to initialize.", _startupException);
        }
    }

    public event EventHandler<HotKeyPressedEventArgs>? HotKeyPressed;

    public IGlobalHotKeyRegistration Register(string name, HotKeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (gesture.VirtualKey is 0 or > 0xFF)
        {
            throw new ArgumentOutOfRangeException(nameof(gesture), "A Win32 virtual-key code from 1 through 255 is required.");
        }

        var id = Interlocked.Increment(ref _nextRegistrationId);
        return InvokeOnMessageThread(() =>
        {
            if (!_native.RegisterHotKey(0, id, gesture.Modifiers, gesture.VirtualKey))
            {
                throw new HotKeyRegistrationException(name, gesture, _native.GetLastError());
            }

            var registration = new Registration(this, id, name, gesture);
            _registrations.Add(id, registration);
            return (IGlobalHotKeyRegistration)registration;
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_thread.IsAlive)
        {
            try
            {
                InvokeOnMessageThread(() =>
                {
                    foreach (var registration in _registrations.Values.ToArray())
                    {
                        UnregisterOnMessageThread(registration, throwOnFailure: false);
                    }

                    return true;
                }, allowDisposed: true);
            }
            finally
            {
                _native.PostThreadMessage(_threadId, NativeConstants.WmQuit, 0, 0);
                if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId &&
                    !_thread.Join(TimeSpan.FromSeconds(5)))
                {
                    Trace.TraceWarning("The global-hotkey thread did not stop in time.");
                }
            }
        }

        _started.Dispose();
        GC.SuppressFinalize(this);
    }

    private void MessageLoop()
    {
        try
        {
            _threadId = _native.GetCurrentThreadId();
            _native.PeekMessage(out _, 0, 0, 0, PeekNoRemove);
            _started.Set();

            while (true)
            {
                var result = _native.GetMessage(out var message, 0, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result < 0)
                {
                    throw new Win32Exception(_native.GetLastError(), "GetMessage failed on the global-hotkey thread.");
                }

                if (message.Message == NativeConstants.WmAppCommand)
                {
                    DrainCommands();
                }
                else if (message.Message == NativeConstants.WmHotKey)
                {
                    RaiseHotKey(unchecked((int)message.WParam));
                }
            }
        }
        catch (Exception exception)
        {
            _startupException ??= exception;
            _started.Set();
            FailPendingCommands(exception);
        }
        finally
        {
            foreach (var registration in _registrations.Values.ToArray())
            {
                UnregisterOnMessageThread(registration, throwOnFailure: false);
            }

            FailPendingCommands(new ObjectDisposedException(nameof(GlobalHotKeyService)));
            _threadId = 0;
        }
    }

    private T InvokeOnMessageThread<T>(Func<T> action, bool allowDisposed = false)
    {
        if (!allowDisposed)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            return action();
        }

        var command = new PendingCommand<T>(action);
        _commands.Enqueue(command);

        if (!_native.PostThreadMessage(_threadId, NativeConstants.WmAppCommand, 0, 0))
        {
            command.Fail(new Win32Exception(
                _native.GetLastError(),
                "Unable to contact the global-hotkey message thread."));
        }

        var completed = Task.WhenAny(command.Task, Task.Delay(TimeSpan.FromSeconds(5)))
            .GetAwaiter()
            .GetResult();
        if (!ReferenceEquals(completed, command.Task))
        {
            var timeout = new TimeoutException("The global-hotkey message thread did not respond in time.");
            command.Fail(timeout);
            throw timeout;
        }

        return command.Task.GetAwaiter().GetResult();
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            command.Execute();
        }
    }

    private void FailPendingCommands(Exception exception)
    {
        while (_commands.TryDequeue(out var command))
        {
            command.Fail(exception);
        }
    }

    private void RaiseHotKey(int id)
    {
        if (!_registrations.TryGetValue(id, out var registration))
        {
            return;
        }

        try
        {
            HotKeyPressed?.Invoke(this, new HotKeyPressedEventArgs(
                registration.Id,
                registration.Name,
                registration.Gesture));
        }
        catch (Exception exception)
        {
            Trace.TraceError("A global-hotkey observer failed: {0}", exception.GetType().Name);
        }
    }

    private void Unregister(Registration registration)
    {
        if (_disposed || registration.IsDisposed)
        {
            return;
        }

        InvokeOnMessageThread(() =>
        {
            UnregisterOnMessageThread(registration, throwOnFailure: true);
            return true;
        });
    }

    private void UnregisterOnMessageThread(Registration registration, bool throwOnFailure)
    {
        if (registration.IsDisposed)
        {
            return;
        }

        if (!_native.UnregisterHotKey(0, registration.Id))
        {
            var error = _native.GetLastError();
            if (throwOnFailure)
            {
                throw new Win32Exception(error, $"Unable to unregister the {registration.Name} global hotkey.");
            }

            Trace.TraceWarning("Unable to unregister a global hotkey during shutdown (error {0}).", error);
            return;
        }

        registration.MarkDisposed();
        _registrations.Remove(registration.Id);
    }

    private sealed class Registration(
        GlobalHotKeyService owner,
        int id,
        string name,
        HotKeyGesture gesture) : IGlobalHotKeyRegistration
    {
        private int _disposed;

        public int Id { get; } = id;

        public string Name { get; } = name;

        public HotKeyGesture Gesture { get; } = gesture;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() => owner.Unregister(this);

        public void MarkDisposed() => Interlocked.Exchange(ref _disposed, 1);
    }

    private interface IPendingCommand
    {
        void Execute();

        void Fail(Exception exception);
    }

    private sealed class PendingCommand<T>(Func<T> action) : IPendingCommand
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimed;

        public Task<T> Task => _completion.Task;

        public void Execute()
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            {
                return;
            }

            try
            {
                _completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
