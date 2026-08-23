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
        var coordinator = new ApplicationShutdownCoordinator(
            lifetime,
            NullLogger<ApplicationShutdownCoordinator>.Instance);

        coordinator.RequestShutdown(ApplicationShutdownKind.Exit);

        Assert.Equal(1, lifetime.StopCount);
        Assert.False(coordinator.RestartRequested);
    }

    [Fact]
    public void RestartUsesTheSameHostShutdownAndSetsPostCleanupLaunchFlag()
    {
        var lifetime = new RecordingLifetime();
        var coordinator = new ApplicationShutdownCoordinator(
            lifetime,
            NullLogger<ApplicationShutdownCoordinator>.Instance);

        coordinator.RequestShutdown(ApplicationShutdownKind.Restart);

        Assert.Equal(1, lifetime.StopCount);
        Assert.True(coordinator.RestartRequested);
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public int StopCount { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            StopCount++;
            stopping.Cancel();
        }
    }
}
