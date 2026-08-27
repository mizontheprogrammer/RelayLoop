using System.Collections.Concurrent;
using RelayLoop.Core;

namespace RelayLoop.Core.Tests;

public sealed class AppStateMachineTests
{
    [Fact]
    public void Recording_FollowsExplicitStopLifecycle()
    {
        var machine = new AppStateMachine();
        var transitions = new List<(AppState Previous, AppState Current)>();
        machine.StateChanged += (_, args) => transitions.Add((args.PreviousState, args.CurrentState));

        machine.BeginRecording();
        machine.RequestStop();
        machine.CompleteStop();

        Assert.Equal(AppState.Idle, machine.State);
        Assert.Equal(
            [
                (AppState.Idle, AppState.Recording),
                (AppState.Recording, AppState.Stopping),
                (AppState.Stopping, AppState.Idle),
            ],
            transitions);
        Assert.Equal(3, machine.TransitionVersion);
    }

    [Fact]
    public void PlaybackAndRecording_CannotStartAtTheSameTime()
    {
        var machine = new AppStateMachine();
        var outcomes = new ConcurrentBag<bool>();
        using var start = new ManualResetEventSlim();

        Parallel.Invoke(
            () =>
            {
                start.Wait();
                outcomes.Add(machine.TryBeginRecording(out _));
            },
            () =>
            {
                start.Wait();
                outcomes.Add(machine.TryBeginPlayback(out _));
            },
            () => start.Set());

        Assert.Single(outcomes, value => value);
        Assert.Single(outcomes, value => !value);
        Assert.True(machine.State is AppState.Recording or AppState.Playing);
    }

    [Fact]
    public void InvalidTransitions_DoNotChangeStateOrVersion()
    {
        var machine = new AppStateMachine();

        var transitioned = machine.TryRequestStop(out var reason);

        Assert.False(transitioned);
        Assert.Contains("Idle", reason, StringComparison.Ordinal);
        Assert.Equal(AppState.Idle, machine.State);
        Assert.Equal(0, machine.TransitionVersion);
        Assert.Throws<InvalidOperationException>(machine.CompleteStop);
    }

    [Fact]
    public void Error_CanBeEnteredFromActiveStateAndExplicitlyReset()
    {
        var machine = new AppStateMachine();
        machine.BeginPlayback();

        machine.SetError("Input injection failed.");

        Assert.Equal(AppState.Error, machine.State);
        Assert.Equal("Input injection failed.", machine.ErrorMessage);
        Assert.False(machine.CanPlay);

        machine.ResetError();

        Assert.Equal(AppState.Idle, machine.State);
        Assert.Null(machine.ErrorMessage);
    }

    [Fact]
    public void StopCanOnlyBeRequestedOnce()
    {
        var machine = new AppStateMachine();
        machine.BeginPlayback();

        Assert.True(machine.TryRequestStop(out _));
        Assert.False(machine.TryRequestStop(out var reason));

        Assert.Equal(AppState.Stopping, machine.State);
        Assert.NotNull(reason);
    }
}
