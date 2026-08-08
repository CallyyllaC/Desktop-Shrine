using DesktopShrine.Plugin.GOverlay;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class GOverlaySettingsTests
{
    [Fact]
    public void DefaultsToAdaptiveRectangleArtwork()
    {
        var configuration = new ConfigurationBuilder().Build();

        var settings = GOverlaySettings.FromConfiguration(configuration);

        Assert.True(settings.DontUseDrawPixels);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ReadsDontUseDrawPixels(string value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DontUseDrawPixels"] = value
            })
            .Build();

        Assert.Equal(
            expected,
            GOverlaySettings.FromConfiguration(configuration)
                .DontUseDrawPixels);
    }

    [Theory]
    [InlineData("IpsSafe", true)]
    [InlineData("Auto", true)]
    [InlineData("Standard", false)]
    [InlineData("Normal", false)]
    public void MigratesLegacyCompatibilitySetting(
        string value,
        bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CompatibilityMode"] = value
            })
            .Build();

        Assert.Equal(
            expected,
            GOverlaySettings.FromConfiguration(configuration)
                .DontUseDrawPixels);
    }

    [Fact]
    public void RejectsInvalidDontUseDrawPixels()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DontUseDrawPixels"] = "sometimes"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            GOverlaySettings.FromConfiguration(configuration));
    }
}
