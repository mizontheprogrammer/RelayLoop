using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RelayLoop.App.Native;

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
public delegate nint LowLevelHookCallback(int code, nuint wParam, nint lParam);

public interface IWindowsHookApi
{
    uint GetCurrentThreadId();

    nint GetModuleHandle(string? moduleName);

    nint SetWindowsHookEx(int hookType, LowLevelHookCallback callback, nint module, uint threadId);

    nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

    bool UnhookWindowsHookEx(nint hook);

    int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);

    bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
}

public sealed class WindowsHookApi : IWindowsHookApi
{
    public uint GetCurrentThreadId() => NativeMethods.GetCurrentThreadId();

    public nint GetModuleHandle(string? moduleName) => NativeMethods.GetModuleHandle(moduleName);

    public nint SetWindowsHookEx(int hookType, LowLevelHookCallback callback, nint module, uint threadId) =>
        NativeMethods.SetWindowsHookEx(hookType, callback, module, threadId);

    public nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam) =>
        NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

    public bool UnhookWindowsHookEx(nint hook) => NativeMethods.UnhookWindowsHookEx(hook);

    public int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum) =>
        NativeMethods.GetMessage(out message, window, minimum, maximum);

    public bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam) =>
        NativeMethods.PostThreadMessage(threadId, message, wParam, lParam);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetWindowsHookEx(
            int idHook,
            LowLevelHookCallback lpfn,
            nint hmod,
            uint dwThreadId);

        [DllImport("user32.dll")]
        internal static extern nint CallNextHookEx(
            nint hhk,
            int nCode,
            nuint wParam,
            nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(nint hhk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetMessage(
            out NativeMessage lpMsg,
            nint hWnd,
            uint wMsgFilterMin,
            uint wMsgFilterMax);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(
            uint idThread,
            uint msg,
            nuint wParam,
            nint lParam);
    }
}

/// <summary>
/// Owns both low-level hooks and the Win32 message queue on a dedicated background thread.
/// Hook callbacks are short and never marshal to the UI thread.
/// </summary>
public interface ILowLevelInputSource : IDisposable
{
    event EventHandler<KeyboardHookEventArgs>? KeyboardInput;

    event EventHandler<MouseHookEventArgs>? MouseInput;

    event EventHandler<Exception>? Faulted;

    bool IsRunning { get; }

    void Start();

    void Stop();
}

public sealed class WindowsLowLevelInputSource : ILowLevelInputSource
{
    private readonly IWindowsHookApi _native;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _thread;
    private LowLevelHookCallback? _keyboardCallback;
    private LowLevelHookCallback? _mouseCallback;
    private nint _keyboardHook;
    private nint _mouseHook;
    private uint _threadId;
    private Exception? _startupException;
    private bool _disposed;

    public WindowsLowLevelInputSource(IWindowsHookApi? native = null)
    {
        _native = native ?? new WindowsHookApi();
    }

    public event EventHandler<KeyboardHookEventArgs>? KeyboardInput;

    public event EventHandler<MouseHookEventArgs>? MouseInput;

    public event EventHandler<Exception>? Faulted;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _thread is { IsAlive: true } && _startupException is null;
            }
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_thread is { IsAlive: true })
            {
                return;
            }

            _started.Reset();
            _startupException = null;
            _thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "RelayLoop low-level input hooks",
            };
            _thread.Start();
        }

        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            Stop();
            throw new TimeoutException("The low-level input hook thread did not initialize in time.");
        }

        if (_startupException is not null)
        {
            throw new InvalidOperationException("Unable to install the low-level input hooks.", _startupException);
        }
    }

    public void Stop()
    {
        Thread? thread;
        uint threadId;

        lock (_gate)
        {
            thread = _thread;
            threadId = _threadId;
        }

        if (thread is null || !thread.IsAlive)
        {
            return;
        }

        if (threadId != 0 && !_native.PostThreadMessage(threadId, NativeConstants.WmQuit, 0, 0))
        {
            Trace.TraceWarning("Unable to post shutdown to the low-level input hook thread (error {0}).", Marshal.GetLastWin32Error());
        }

        if (Environment.CurrentManagedThreadId != thread.ManagedThreadId &&
            !thread.Join(TimeSpan.FromSeconds(5)))
        {
            Trace.TraceWarning("The low-level input hook thread did not stop in time.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _started.Dispose();
        GC.SuppressFinalize(this);
    }

    private void MessageLoop()
    {
        try
        {
            _threadId = _native.GetCurrentThreadId();
            _keyboardCallback = KeyboardHookProcedure;
            _mouseCallback = MouseHookProcedure;
            var module = _native.GetModuleHandle(null);

            _keyboardHook = _native.SetWindowsHookEx(
                NativeConstants.WhKeyboardLl,
                _keyboardCallback,
                module,
                0);
            if (_keyboardHook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) failed.");
            }

            _mouseHook = _native.SetWindowsHookEx(
                NativeConstants.WhMouseLl,
                _mouseCallback,
                module,
                0);
            if (_mouseHook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_MOUSE_LL) failed.");
            }

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
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage failed on the input hook thread.");
                }
            }
        }
        catch (Exception exception)
        {
            _startupException ??= exception;
            _started.Set();
            RaiseFaulted(exception);
        }
        finally
        {
            if (_mouseHook != 0)
            {
                _native.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = 0;
            }

            if (_keyboardHook != 0)
            {
                _native.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = 0;
            }

            _keyboardCallback = null;
            _mouseCallback = null;
            _threadId = 0;
        }
    }

    private nint KeyboardHookProcedure(int code, nuint wParam, nint lParam)
    {
        if (code >= 0 && Enum.IsDefined(typeof(KeyboardWindowMessage), unchecked((uint)wParam)))
        {
            try
            {
                var value = Marshal.PtrToStructure<KeyboardHookData>(lParam);
                var input = new NativeKeyboardEvent(
                    (KeyboardWindowMessage)unchecked((uint)wParam),
                    value.VirtualKey,
                    value.ScanCode,
                    value.Flags,
                    value.Time,
                    value.ExtraInfo);
                var args = new KeyboardHookEventArgs(input);
                KeyboardInput?.Invoke(this, args);
                if (args.Suppress)
                {
                    return 1;
                }
            }
            catch (Exception exception)
            {
                RaiseFaulted(exception);
            }
        }

        return _native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private nint MouseHookProcedure(int code, nuint wParam, nint lParam)
    {
        if (code >= 0 && Enum.IsDefined(typeof(MouseWindowMessage), unchecked((uint)wParam)))
        {
            try
            {
                var value = Marshal.PtrToStructure<MouseHookData>(lParam);
                var input = new NativeMouseEvent(
                    (MouseWindowMessage)unchecked((uint)wParam),
                    value.Point.X,
                    value.Point.Y,
                    value.MouseData,
                    value.Flags,
                    value.Time,
                    value.ExtraInfo);
                var args = new MouseHookEventArgs(input);
                MouseInput?.Invoke(this, args);
                if (args.Suppress)
                {
                    return 1;
                }
            }
            catch (Exception exception)
            {
                RaiseFaulted(exception);
            }
        }

        return _native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void RaiseFaulted(Exception exception)
    {
        try
        {
            Faulted?.Invoke(this, exception);
        }
        catch (Exception observerException)
        {
            Trace.TraceError("An input-hook error observer failed: {0}", observerException.GetType().Name);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KeyboardHookData
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly LowLevelKeyboardFlags Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseHookData
    {
        public readonly NativePoint Point;
        public readonly uint MouseData;
        public readonly LowLevelMouseFlags Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }
}
