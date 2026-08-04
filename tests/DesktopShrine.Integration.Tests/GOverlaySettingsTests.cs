using DesktopShrine.Plugin.GOverlay;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class GOverlaySettingsTests
{
    [Fact]
    public void ReadsIpsCompatibilityBudgets()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CompatibilityMode"] = "IpsSafe",
                ["MaximumCommandsPerRefresh"] = "40",
                ["MaximumArtworkBatchesPerRefresh"] = "2",
                ["MaximumDrawMilliseconds"] = "80",
                ["AudioRefreshDivisor"] = "2",
                ["ReconnectStabilizationMilliseconds"] = "2000"
            })
            .Build();

        var settings = GOverlaySettings.FromConfiguration(configuration);

        Assert.Equal("IpsSafe", settings.CompatibilityMode);
        Assert.Equal(40, settings.MaximumCommandsPerRefresh);
        Assert.Equal(2, settings.MaximumArtworkBatchesPerRefresh);
        Assert.Equal(80, settings.MaximumDrawMilliseconds);
        Assert.Equal(2, settings.AudioRefreshDivisor);
        Assert.Equal(2000, settings.ReconnectStabilizationMilliseconds);
    }

    [Theory]
    [InlineData("CompatibilityMode", "unsafe")]
    [InlineData("MaximumCommandsPerRefresh", "7")]
    [InlineData("MaximumArtworkBatchesPerRefresh", "0")]
    [InlineData("MaximumDrawMilliseconds", "9")]
    [InlineData("AudioRefreshDivisor", "0")]
    [InlineData("ReconnectStabilizationMilliseconds", "249")]
    public void RejectsUnsafeRendererSettings(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = value
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => GOverlaySettings.FromConfiguration(configuration));
    }
}
