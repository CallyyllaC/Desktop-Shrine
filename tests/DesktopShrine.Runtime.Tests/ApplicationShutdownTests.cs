using DesktopShrine.Abstractions;
using DesktopShrine.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopShrine.Runtime.Tests;

public sealed class ApplicationShutdownTests
{
    [Fact]
    public void RestartHandoffRoundTripsForwardedArguments()
    {
        var arguments = RestartProcessHandoff.CreateArguments(
            1234,
            ["--launch", "value with spaces"]);

        var parsed = RestartProcessHandoff.TryParse(
            arguments,
            out var processId,
            out var forwardedArguments);

        Assert.True(parsed);
        Assert.Equal(1234, processId);
        Assert.Equal(["--launch", "value with spaces"], forwardedArguments);
    }

    [Fact]
    public void OrdinaryLaunchIsNotMistakenForRestartHandoff()
    {
        Assert.False(RestartProcessHandoff.TryParse(
            ["--launch"],
            out _,
            out _));
    }

    [Fact]
    public void ExitRequestsTheSharedHostShutdownWithoutRestart()
    {
        var lifetime = new RecordingLifetime();
        var outputShutdown = CreateOutputShutdown();
        var output = new RecordingOutput();
        outputShutdown.Register(output);
        lifetime.IsBlackoutComplete = () => output.BlackoutCount == 1;
        var coordinator = new ApplicationShutdownCoordinator(
            lifetime,
            outputShutdown,
            NullLogger<ApplicationShutdownCoordinator>.Instance);

        coordinator.RequestShutdown(ApplicationShutdownKind.Exit);

        Assert.Equal(1, lifetime.StopCount);
        Assert.True(lifetime.BlackoutCompleteWhenStopped);
        Assert.Equal(1, output.MuteCount);
        Assert.Equal(1, output.BlackoutCount);
        Assert.False(coordinator.RestartRequested);
    }

    [Fact]
    public void RestartUsesTheSameHostShutdownAndSetsPostCleanupLaunchFlag()
    {
        var lifetime = new RecordingLifetime();
        var coordinator = new ApplicationShutdownCoordinator(
            lifetime,
            CreateOutputShutdown(),
            NullLogger<ApplicationShutdownCoordinator>.Instance);

        coordinator.RequestShutdown(ApplicationShutdownKind.Restart);

        Assert.Equal(1, lifetime.StopCount);
        Assert.True(coordinator.RestartRequested);
    }

    [Fact]
    public void BlackoutIsIdempotentAndContinuesPastFailingOutputs()
    {
        var outputShutdown = CreateOutputShutdown();
        var failing = new RecordingOutput
        {
            ThrowWhenMuting = true,
            ThrowWhenBlackouting = true
        };
        var healthy = new RecordingOutput();
        outputShutdown.Register(failing);
        outputShutdown.Register(healthy);

        outputShutdown.BeginShutdown();
        outputShutdown.BeginShutdown();

        Assert.True(outputShutdown.ShutdownInProgress);
        Assert.Equal(1, failing.MuteCount);
        Assert.Equal(1, failing.BlackoutCount);
        Assert.Equal(1, healthy.MuteCount);
        Assert.Equal(1, healthy.BlackoutCount);
    }

    [Fact]
    public void NormalApplicationStoppingAlsoBlackoutsOutputs()
    {
        var lifetime = new RecordingLifetime();
        var outputShutdown = CreateOutputShutdown();
        var output = new RecordingOutput();
        outputShutdown.Register(output);
        _ = new ApplicationShutdownCoordinator(
            lifetime,
            outputShutdown,
            NullLogger<ApplicationShutdownCoordinator>.Instance);

        lifetime.SignalStopping();

        Assert.Equal(1, output.MuteCount);
        Assert.Equal(1, output.BlackoutCount);
    }

    [Fact]
    public void OutputRegisteredDuringShutdownStartsMutedAndBlackened()
    {
        var outputShutdown = CreateOutputShutdown();
        outputShutdown.BeginShutdown();
        var lateOutput = new RecordingOutput();

        outputShutdown.Register(lateOutput);

        Assert.Equal(1, lateOutput.MuteCount);
        Assert.Equal(1, lateOutput.BlackoutCount);
    }

    private static OutputShutdownCoordinator CreateOutputShutdown() => new(
        NullLogger<OutputShutdownCoordinator>.Instance);

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public int StopCount { get; private set; }
        public Func<bool>? IsBlackoutComplete { get; set; }
        public bool BlackoutCompleteWhenStopped { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            StopCount++;
            BlackoutCompleteWhenStopped = IsBlackoutComplete?.Invoke() ?? false;
            stopping.Cancel();
        }

        public void SignalStopping() => stopping.Cancel();
    }

    private sealed class RecordingOutput : IShutdownOutputParticipant
    {
        public int MuteCount { get; private set; }
        public int BlackoutCount { get; private set; }
        public bool ThrowWhenMuting { get; init; }
        public bool ThrowWhenBlackouting { get; init; }

        public void MuteOutputForShutdown()
        {
            MuteCount++;
            if (ThrowWhenMuting)
                throw new InvalidOperationException("Mute failed.");
        }

        public void BlackoutForShutdown()
        {
            BlackoutCount++;
            if (ThrowWhenBlackouting)
                throw new InvalidOperationException("Blackout failed.");
        }
    }
}
