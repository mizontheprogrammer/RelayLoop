using RelayLoop.App.Native;
using Xunit;

namespace RelayLoop.IntegrationTests;

public sealed class CursorLockServiceTests
{
    [Fact]
    public void LockAtAndRelease_ConfinesOnePixelThenRestoresPreviousBoundary()
    {
        var original = new NativeRectangle(-1920, 0, 1920, 1080);
        FakeCursorClipNative native = new(original);
        using CursorLockService service = new(native);

        service.LockAt(420, 315);

        Assert.True(service.IsLocked);
        Assert.Equal(new NativeRectangle(420, 315, 421, 316), native.CurrentClip);

        service.Release();

        Assert.False(service.IsLocked);
        Assert.Equal(original, native.CurrentClip);
        Assert.False(native.WasUnconditionallyReleased);
    }

    [Fact]
    public void Release_FallsBackToRemovingClipIfPreviousBoundaryCannotBeRestored()
    {
        FakeCursorClipNative native = new(new NativeRectangle(0, 0, 1920, 1080));
        using CursorLockService service = new(native);
        service.LockAt(100, 200);
        native.FailNextApply = true;

        service.Release();

        Assert.False(service.IsLocked);
        Assert.True(native.WasUnconditionallyReleased);
    }

    private sealed class FakeCursorClipNative(NativeRectangle initialClip) : ICursorClipNativeFacade
    {
        public NativeRectangle CurrentClip { get; private set; } = initialClip;
        public bool FailNextApply { get; set; }
        public bool WasUnconditionallyReleased { get; private set; }

        public bool GetClip(out NativeRectangle rectangle)
        {
            rectangle = CurrentClip;
            return true;
        }

        public bool ApplyClip(NativeRectangle rectangle)
        {
            if (FailNextApply)
            {
                FailNextApply = false;
                return false;
            }

            CurrentClip = rectangle;
            return true;
        }

        public bool ReleaseClip()
        {
            WasUnconditionallyReleased = true;
            return true;
        }

        public int GetLastError() => 5;
    }
}
