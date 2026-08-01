using DesktopShrine.Abstractions;
using DesktopShrine.Plugin.ArtworkPalette;
using DesktopShrine.Plugin.HardwareMonitor;
using DesktopShrine.Plugin.AudioCollector;
using DesktopShrine.Plugin.BlinkStickBar;
using DesktopShrine.Plugin.ConsoleDisplay;
using DesktopShrine.Plugin.GOverlay;
using DesktopShrine.Plugin.SteamNowPlaying;
using DesktopShrine.Plugin.WindowsNowPlaying;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class SamplePluginTests
{
    [Fact]
    public void SamplesDeclareCompatiblePorts()
    {
        var nowPlaying = new WindowsNowPlayingPlugin();
        var hardware = new HardwareMonitorPlugin();
        var steamNowPlaying = new SteamNowPlayingPlugin();
        var audio = new AudioCollectorPlugin();
        var palette = new ArtworkPalettePlugin();
        var display = new ConsoleDisplayPlugin();
        var blinkStick = new BlinkStickBarPlugin();
        var gOverlay = new GOverlayPlugin();
        var nowPlayingContract = nowPlaying.ProvidedPorts.Single().Contract;

        var mediaDisplayPort = display.RequiredPorts.Single(
            port => port.Requirement.ContractId == "desktop-shrine.media.now-playing");
        var audioDisplayPort = display.RequiredPorts.Single(
            port => port.Requirement.ContractId == "desktop-shrine.audio.spectrum");

        Assert.True(mediaDisplayPort.Requirement.AcceptedVersions.Contains(nowPlayingContract.Version));
        Assert.True(mediaDisplayPort.Requirement.AcceptedVersions.Contains(
            steamNowPlaying.ProvidedPorts.Single().Contract.Version));
        Assert.True(audioDisplayPort.Requirement.AcceptedVersions.Contains(
            audio.ProvidedPorts.Single().Contract.Version));
        Assert.True(palette.RequiredPorts.Single().Requirement.AcceptedVersions.Contains(nowPlayingContract.Version));
        Assert.Equal("desktop-shrine.media.colour-palette", palette.ProvidedPorts.Single().Contract.ContractId);
        Assert.Contains("windows", nowPlaying.Descriptor.SupportedPlatforms);
        Assert.Contains("windows", hardware.Descriptor.SupportedPlatforms);
        Assert.Equal(
            "desktop-shrine.hardware.monitor",
            hardware.ProvidedPorts.Single().Contract.ContractId);
        Assert.Equal(
            InputActivityState.Inactive,
            hardware.ActivityState);
        Assert.Equal(
            DefaultInputPriorityPlacement.Lowest,
            hardware.DefaultPriorityPlacement);
        Assert.Contains("windows", steamNowPlaying.Descriptor.SupportedPlatforms);
        Assert.Equal(
            InputActivityState.Inactive,
            steamNowPlaying.ActivityState);
        Assert.Equal(
            DefaultInputPriorityPlacement.Highest,
            steamNowPlaying.DefaultPriorityPlacement);
        Assert.Equal("desktop-shrine.audio.spectrum", audio.ProvidedPorts.Single().Contract.ContractId);
        Assert.Contains("windows", audio.Descriptor.SupportedPlatforms);
        Assert.Contains("windows", palette.Descriptor.SupportedPlatforms);
        Assert.Contains("windows", blinkStick.Descriptor.SupportedPlatforms);
        Assert.Equal(4, blinkStick.RequiredPorts.Count);
        Assert.Contains(
            blinkStick.RequiredPorts,
            port => port.Requirement.ContractId
                == "desktop-shrine.media.now-playing");
        Assert.Contains(
            blinkStick.RequiredPorts,
            port => port.Requirement.ContractId
                == "desktop-shrine.media.colour-palette");
        Assert.Contains(
            blinkStick.RequiredPorts,
            port => port.Requirement.ContractId
                == "desktop-shrine.audio.spectrum");
        Assert.Contains(
            blinkStick.RequiredPorts,
            port => port.Requirement.ContractId
                == "desktop-shrine.hardware.monitor");
        Assert.Contains("windows", gOverlay.Descriptor.SupportedPlatforms);
        Assert.Contains(
            gOverlay.RequiredPorts,
            port => port.Requirement.ContractId == "desktop-shrine.media.now-playing");
        Assert.Contains(
            gOverlay.RequiredPorts,
            port => port.Requirement.ContractId == "desktop-shrine.media.colour-palette");
        Assert.Contains(
            gOverlay.RequiredPorts,
            port => port.Requirement.ContractId == "desktop-shrine.audio.spectrum");
        Assert.Contains(
            gOverlay.RequiredPorts,
            port => port.Requirement.ContractId == "desktop-shrine.hardware.monitor"
                && !port.IsRequired);
    }
}
