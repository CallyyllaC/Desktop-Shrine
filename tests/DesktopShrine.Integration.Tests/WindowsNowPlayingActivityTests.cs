using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Plugin.WindowsNowPlaying;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class WindowsNowPlayingActivityTests
{
    [Fact]
    public async Task PausedInputDiscoveredAtStartupIsInactive()
    {
        await using var plugin = new WindowsNowPlayingPlugin(
            new()
            {
                PausedTimeoutSeconds = 1
            });

        plugin.UpdateActivity(PlaybackStatus.Paused);

        Assert.Equal(InputActivityState.Inactive, plugin.ActivityState);

        await Task.Delay(
            TimeSpan.FromMilliseconds(1_200),
            TestContext.Current.CancellationToken);

        Assert.Equal(InputActivityState.Inactive, plugin.ActivityState);
    }

    [Fact]
    public async Task PausedInputStaysActiveUntilConfiguredTimeout()
    {
        await using var plugin = new WindowsNowPlayingPlugin(
            new()
            {
                PausedTimeoutSeconds = 1
            });

        plugin.UpdateActivity(PlaybackStatus.Playing);
        plugin.UpdateActivity(PlaybackStatus.Paused);

        Assert.Equal(InputActivityState.Active, plugin.ActivityState);

        await Task.Delay(
            TimeSpan.FromMilliseconds(1_200),
            TestContext.Current.CancellationToken);

        Assert.Equal(InputActivityState.Inactive, plugin.ActivityState);
    }

    [Fact]
    public async Task ResumingCancelsPausedTimeout()
    {
        await using var plugin = new WindowsNowPlayingPlugin(
            new()
            {
                PausedTimeoutSeconds = 1
            });

        plugin.UpdateActivity(PlaybackStatus.Playing);
        plugin.UpdateActivity(PlaybackStatus.Paused);
        plugin.UpdateActivity(PlaybackStatus.Playing);

        await Task.Delay(
            TimeSpan.FromMilliseconds(1_200),
            TestContext.Current.CancellationToken);

        Assert.Equal(InputActivityState.Active, plugin.ActivityState);
    }

    [Fact]
    public async Task ZeroTimeoutMakesPauseImmediatelyInactive()
    {
        await using var plugin = new WindowsNowPlayingPlugin(
            new()
            {
                PausedTimeoutSeconds = 0
            });

        plugin.UpdateActivity(PlaybackStatus.Playing);
        plugin.UpdateActivity(PlaybackStatus.Paused);

        Assert.Equal(InputActivityState.Inactive, plugin.ActivityState);
    }
}
