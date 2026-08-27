using System.ComponentModel;
using RelayLoop.Runner;
using Xunit;

namespace RelayLoop.IntegrationTests;

public sealed class StandaloneInputPlayerTests
{
    [Fact]
    public async Task StopAsync_CancelsInfiniteWait_AndReleasesTrackedKeyAndMouseButton()
    {
        FakeRunnerInputNativeFacade native = new();
        using StandaloneInputPlayer player = new(native);
        RunnerInputAction[] actions =
        [
            new(RunnerInputActionKind.KeyDown, TimeSpan.Zero, VirtualKey: 0x41, ScanCode: 0x1E),
            new(RunnerInputActionKind.LeftButtonDown, TimeSpan.Zero, X: -200, Y: 300),
            new(RunnerInputActionKind.KeyUp, TimeSpan.FromMinutes(1), VirtualKey: 0x41, ScanCode: 0x1E),
        ];

        Task playback = player.PlayAsync(actions, progress: null, CancellationToken.None);
        await native.HeldInputsSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        RunnerInputReleaseException? releaseFailure = await player.StopAsync();

        Assert.Null(releaseFailure);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
        Assert.Contains(native.Sent, IsKeyboardUp);
        Assert.Contains(native.Sent, input =>
            input.Type == NativeMethods.InputMouse &&
            (input.Data.Mouse.Flags & NativeMethods.MouseEventLeftUp) != 0);
    }

    [Fact]
    public async Task PlayAsync_HorizontalWheel_UsesHorizontalWheelPacket()
    {
        FakeRunnerInputNativeFacade native = new();
        using StandaloneInputPlayer player = new(native);

        await player.PlayAsync(
            [new(RunnerInputActionKind.HorizontalWheel, TimeSpan.Zero, X: -100, Y: 200, Data: -120)],
            progress: null,
            CancellationToken.None);

        NativeMethods.Input wheel = Assert.Single(native.Sent, input =>
            input.Type == NativeMethods.InputMouse &&
            (input.Data.Mouse.Flags & NativeMethods.MouseEventHorizontalWheel) != 0);
        Assert.Equal(unchecked((uint)-120), wheel.Data.Mouse.MouseData);
    }

    [Fact]
    public async Task PlayAsync_ReleaseFailure_IsSurfaced_AndRemainsTrackedForRetry()
    {
        FakeRunnerInputNativeFacade native = new()
        {
            ReleaseFailuresRemaining = 1,
        };
        using StandaloneInputPlayer player = new(native);

        RunnerInputReleaseException exception = await Assert.ThrowsAsync<RunnerInputReleaseException>(() =>
            player.PlayAsync(
                [new(RunnerInputActionKind.KeyDown, TimeSpan.Zero, VirtualKey: 0x41, ScanCode: 0x1E)],
                progress: null,
                CancellationToken.None));

        Assert.Equal(1, exception.RemainingInputCount);
        Assert.Single(exception.ReleaseFailures);

        RunnerInputReleaseException? retryFailure = await player.StopAsync();

        Assert.Null(retryFailure);
        Assert.Equal(2, native.ReleaseAttempts);
        Assert.Contains(native.Sent, IsKeyboardUp);
    }

    private static bool IsKeyboardUp(NativeMethods.Input input) =>
        input.Type == NativeMethods.InputKeyboard &&
        (input.Data.Keyboard.Flags & NativeMethods.KeyEventKeyUp) != 0;

    private sealed class FakeRunnerInputNativeFacade : IRunnerInputNativeFacade
    {
        private readonly object _gate = new();
        private int _heldDownCount;

        internal List<NativeMethods.Input> Sent { get; } = [];

        internal TaskCompletionSource<bool> HeldInputsSent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ReleaseFailuresRemaining { get; set; }

        internal int ReleaseAttempts { get; private set; }

        public int GetSystemMetric(int index) => index switch
        {
            NativeMethods.SmXVirtualScreen => -1920,
            NativeMethods.SmYVirtualScreen => 0,
            NativeMethods.SmCxVirtualScreen => 3840,
            NativeMethods.SmCyVirtualScreen => 1080,
            _ => 0,
        };

        public void Send(NativeMethods.Input input)
        {
            lock (_gate)
            {
                bool isRelease = IsKeyboardUp(input) ||
                                 (input.Type == NativeMethods.InputMouse &&
                                  (input.Data.Mouse.Flags &
                                   (NativeMethods.MouseEventLeftUp |
                                    NativeMethods.MouseEventRightUp |
                                    NativeMethods.MouseEventMiddleUp |
                                    NativeMethods.MouseEventXUp)) != 0);
                if (isRelease)
                {
                    ReleaseAttempts++;
                    if (ReleaseFailuresRemaining > 0)
                    {
                        ReleaseFailuresRemaining--;
                        throw new Win32Exception(5, "Simulated SendInput release rejection.");
                    }
                }

                Sent.Add(input);
                bool isKeyDown = input.Type == NativeMethods.InputKeyboard &&
                                 (input.Data.Keyboard.Flags & NativeMethods.KeyEventKeyUp) == 0;
                bool isLeftDown = input.Type == NativeMethods.InputMouse &&
                                  (input.Data.Mouse.Flags & NativeMethods.MouseEventLeftDown) != 0;
                if (isKeyDown || isLeftDown)
                {
                    _heldDownCount++;
                    if (_heldDownCount >= 2)
                    {
                        HeldInputsSent.TrySetResult(true);
                    }
                }
            }
        }
    }
}
