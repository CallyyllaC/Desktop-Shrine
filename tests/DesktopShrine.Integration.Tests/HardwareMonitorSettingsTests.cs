using DesktopShrine.Plugin.HardwareMonitor;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class HardwareMonitorSettingsTests
{
    [Fact]
    public void ReadsOneSecondIntervalAndSampleLogging()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PollIntervalMilliseconds"] = "1000",
                ["LogEverySample"] = "true"
            })
            .Build();

        var settings =
            HardwareMonitorSettings.FromConfiguration(configuration);

        Assert.Equal(1000, settings.PollIntervalMilliseconds);
        Assert.True(settings.LogEverySample);
    }

    [Fact]
    public void AnyOtherPollingIntervalIsRejected()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PollIntervalMilliseconds"] = "500"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => HardwareMonitorSettings.FromConfiguration(
                configuration));

        Assert.Contains(
            "must be 1000",
            exception.Message);
    }
}
