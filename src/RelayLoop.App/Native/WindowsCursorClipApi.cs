using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RelayLoop.App.Native;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeRectangle(int Left, int Top, int Right, int Bottom);

public interface ICursorClipNativeFacade
{
    bool GetClip(out NativeRectangle rectangle);

    bool ApplyClip(NativeRectangle rectangle);

    bool ReleaseClip();

    int GetLastError();
}

public sealed class WindowsCursorClipNative : ICursorClipNativeFacade
{
    public bool GetClip(out NativeRectangle rectangle) => GetClipCursor(out rectangle);

    public bool ApplyClip(NativeRectangle rectangle) => ClipCursor(ref rectangle);

    public bool ReleaseClip() => ClipCursor(nint.Zero);

    public int GetLastError() => Marshal.GetLastWin32Error();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClipCursor(out NativeRectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref NativeRectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(nint rectangle);
}

/// <summary>Temporarily confines the system pointer to one physical screen pixel.</summary>
public sealed class CursorLockService(ICursorClipNativeFacade? native = null) : IDisposable
{
    private readonly ICursorClipNativeFacade _native = native ?? new WindowsCursorClipNative();
    private NativeRectangle _previousClip;

    public bool IsLocked { get; private set; }

    public void LockAt(int x, int y)
    {
        if (IsLocked)
        {
            Release();
        }

        if (!_native.GetClip(out var previous))
        {
            throw new Win32Exception(_native.GetLastError(), "Windows could not read the current mouse boundary.");
        }

        NativeRectangle target;
        try
        {
            target = new NativeRectangle(x, y, checked(x + 1), checked(y + 1));
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "The mouse-lock position is outside the supported coordinate range.");
        }

        if (!_native.ApplyClip(target))
        {
            throw new Win32Exception(_native.GetLastError(), "Windows could not lock the mouse position.");
        }

        _previousClip = previous;
        IsLocked = true;
    }

    public void Release()
    {
        if (!IsLocked)
        {
            return;
        }

        if (_native.ApplyClip(_previousClip) || _native.ReleaseClip())
        {
            IsLocked = false;
            return;
        }

        throw new Win32Exception(_native.GetLastError(), "Windows could not release the mouse-position lock.");
    }

    public void Dispose() => Release();
}
