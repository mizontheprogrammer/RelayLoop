using System.Windows.Automation;
using RelayLoop.App.Native;

namespace RelayLoop.App.Services;

public enum SensitiveInputBlockReason
{
    None,
    Initializing,
    DifferentDesktop,
    PasswordField,
    InspectionFailed,
}

public readonly record struct SensitiveInputState(bool CanRecordKeyboard, SensitiveInputBlockReason BlockReason)
{
    public static SensitiveInputState Allowed { get; } = new(true, SensitiveInputBlockReason.None);
}

public interface ISensitiveInputGuard : IDisposable
{
    SensitiveInputState CurrentState { get; }
}

/// <summary>
/// Polls focus away from the low-level hook callback. The cached state starts blocked and every
/// inspection error fails closed. It never reads a value, name, or other field content.
/// </summary>
public sealed class WindowsSensitiveInputGuard : ISensitiveInputGuard
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private readonly ISensitiveInputNativeFacade _native;
    private readonly Timer _timer;
    private SensitiveInputState _state = new(false, SensitiveInputBlockReason.Initializing);
    private nint _inspectedWindow;
    private int _checking;
    private bool _disposed;

    public WindowsSensitiveInputGuard(ISensitiveInputNativeFacade? native = null)
    {
        _native = native ?? new WindowsSensitiveInputApi();
        _timer = new Timer(Refresh, null, TimeSpan.Zero, PollInterval);
    }

    public SensitiveInputState CurrentState
    {
        get
        {
            try
            {
                var focusedWindow = _native.GetFocusedWindow();
                var inspectedWindow = Interlocked.CompareExchange(ref _inspectedWindow, 0, 0);
                return focusedWindow != 0 && focusedWindow == inspectedWindow
                    ? _state
                    : new SensitiveInputState(false, SensitiveInputBlockReason.Initializing);
            }
            catch
            {
                return new SensitiveInputState(false, SensitiveInputBlockReason.InspectionFailed);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Refresh(object? state)
    {
        if (_disposed || Interlocked.Exchange(ref _checking, 1) != 0)
        {
            return;
        }

        try
        {
            _state = new SensitiveInputState(false, SensitiveInputBlockReason.Initializing);
            if (!_native.IsCurrentInputDesktop())
            {
                _state = new SensitiveInputState(false, SensitiveInputBlockReason.DifferentDesktop);
                Interlocked.Exchange(ref _inspectedWindow, 0);
                return;
            }

            var focusedWindow = _native.GetFocusedWindow();
            if (focusedWindow == 0)
            {
                _state = new SensitiveInputState(false, SensitiveInputBlockReason.InspectionFailed);
                Interlocked.Exchange(ref _inspectedWindow, 0);
                return;
            }

            if (_native.IsStandardPasswordEdit(focusedWindow))
            {
                _state = new SensitiveInputState(false, SensitiveInputBlockReason.PasswordField);
                Interlocked.Exchange(ref _inspectedWindow, focusedWindow);
                return;
            }

            if (IsAutomationPasswordField(focusedWindow))
            {
                _state = new SensitiveInputState(false, SensitiveInputBlockReason.PasswordField);
                Interlocked.Exchange(ref _inspectedWindow, focusedWindow);
                return;
            }

            _state = SensitiveInputState.Allowed;
            Interlocked.Exchange(ref _inspectedWindow, focusedWindow);
        }
        catch
        {
            _state = new SensitiveInputState(false, SensitiveInputBlockReason.InspectionFailed);
            Interlocked.Exchange(ref _inspectedWindow, 0);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private static bool IsAutomationPasswordField(nint focusedWindow)
    {
        AutomationElement? element;
        try
        {
            element = AutomationElement.FocusedElement;
        }
        catch (ElementNotAvailableException)
        {
            element = focusedWindow == 0 ? null : AutomationElement.FromHandle(focusedWindow);
        }

        if (element is null)
        {
            throw new InvalidOperationException("The focused control could not be inspected safely.");
        }

        var value = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, ignoreDefaultValue: true);
        return value is bool isPassword && isPassword;
    }
}

public sealed class SensitiveInputBlockedEventArgs(SensitiveInputBlockReason reason) : EventArgs
{
    public SensitiveInputBlockReason Reason { get; } = reason;
}
