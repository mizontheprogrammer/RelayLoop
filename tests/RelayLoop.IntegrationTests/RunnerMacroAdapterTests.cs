using RelayLoop.Core;
using RelayLoop.Runner;
using Xunit;

namespace RelayLoop.IntegrationTests;

public sealed class RunnerMacroAdapterTests
{
    [Fact]
    public void Create_PreservesDisabledEventDelay_AndUsesExecutableName()
    {
        MacroDocument document = new()
        {
            Events =
            [
                new MacroEvent
                {
                    Kind = MacroEventKind.MouseMove,
                    DelayMicroseconds = 10,
                    Enabled = false,
                    X = -500,
                    Y = 20,
                },
                new MacroEvent
                {
                    Kind = MacroEventKind.KeyDown,
                    DelayMicroseconds = 20,
                    VirtualKey = 0x41,
                    ScanCode = 0x1E,
                },
            ],
        };

        RunnerMacroData result = RunnerMacroAdapter.Create(document, @"C:\Exports\Night Shift.exe");

        Assert.Equal("Night Shift", result.Name);
        Assert.Equal(TimeSpan.FromTicks(300), result.Duration);
        RunnerInputAction action = Assert.Single(result.Actions);
        Assert.Equal(RunnerInputActionKind.KeyDown, action.Kind);
        Assert.Equal(TimeSpan.FromTicks(300), action.Offset);
        Assert.Equal((ushort)0x41, action.VirtualKey);
        Assert.Equal((ushort)0x1E, action.ScanCode);
    }

    [Theory]
    [InlineData(false, 11)]
    [InlineData(true, 12)]
    public void Create_PreservesWheelDirection(bool isHorizontal, int expectedKind)
    {
        MacroDocument document = new()
        {
            Events =
            [
                new MacroEvent
                {
                    Kind = MacroEventKind.MouseWheel,
                    WheelDelta = -120,
                    IsHorizontalWheel = isHorizontal,
                    X = -320,
                    Y = 240,
                },
            ],
        };

        RunnerInputAction action = Assert.Single(
            RunnerMacroAdapter.Create(document, @"C:\Exports\Wheel.exe").Actions);

        Assert.Equal((RunnerInputActionKind)expectedKind, action.Kind);
        Assert.Equal(-120, action.Data);
        Assert.Equal(-320, action.X);
        Assert.Equal(240, action.Y);
    }

    [Theory]
    [InlineData(-1920, -1920, 3840, 0)]
    [InlineData(1919, -1920, 3840, 65535)]
    [InlineData(-5000, -1920, 3840, 0)]
    [InlineData(5000, -1920, 3840, 65535)]
    public void NormalizeAbsoluteCoordinate_HandlesNegativeVirtualDesktop(
        int coordinate,
        int origin,
        int extent,
        int expected)
    {
        int actual = StandaloneInputPlayer.NormalizeAbsoluteCoordinate(coordinate, origin, extent);

        Assert.Equal(expected, actual);
    }
}
