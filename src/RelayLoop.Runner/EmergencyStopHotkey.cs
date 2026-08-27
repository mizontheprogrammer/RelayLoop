using System.ComponentModel;
using System.Windows.Interop;

namespace RelayLoop.Runner;

internal sealed class EmergencyStopHotkey : IDisposable
{
    private const int HotkeyId = 0x524C;
    private HwndSource? _source;
    private IntPtr _window;
    private Action? _onPressed;
    private bool _registered;

    internal void Register(IntPtr window, Action onPressed)
    {
        ObjectDisposedException.ThrowIf(_source is null && _window != IntPtr.Zero, this);

        if (_registered)
        {
            throw new InvalidOperationException("The emergency-stop hotkey is already registered.");
        }

        _source = HwndSource.FromHwnd(window)
            ?? throw new InvalidOperationException("The runner window is not ready to register its emergency-stop hotkey.");
        _window = window;
        _onPressed = onPressed ?? throw new ArgumentNullException(nameof(onPressed));
        _source.AddHook(WindowProcedure);

        uint modifiers = NativeMethods.ModControl | NativeMethods.ModShift |
                         NativeMethods.ModAlt | NativeMethods.ModNoRepeat;
        if (!NativeMethods.RegisterHotKey(window, HotkeyId, modifiers, NativeMethods.VkS))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _source.RemoveHook(WindowProcedure);
            _source = null;
            _window = IntPtr.Zero;
            _onPressed = null;
            throw new Win32Exception(error,
                "Ctrl+Shift+Alt+S is already in use. Playback is disabled until that hotkey is available.");
        }

        _registered = true;
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _onPressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            _ = NativeMethods.UnregisterHotKey(_window, HotkeyId);
            _registered = false;
        }

        _source?.RemoveHook(WindowProcedure);
        _source = null;
        _onPressed = null;
        _window = IntPtr.Zero;
    }
}
