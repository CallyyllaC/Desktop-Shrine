using DesktopShrine.Plugin.GOverlay;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class GOverlayDeviceTests
{
    [Theory]
    [InlineData(
        @"USB\VID_20A0&PID_41EC\LCDSZ",
        GOverlayDeviceModel.LcdSysInfo28,
        320,
        240,
        "LCDSZ")]
    [InlineData(
        @"USB\VID_20A0&PID_41ED\2CDSZ",
        GOverlayDeviceModel.LcdSysInfo35,
        480,
        320,
        "2CDSZ")]
    [InlineData(
        @"usb\vid_20a0&pid_41ed\lowercase",
        GOverlayDeviceModel.LcdSysInfo35,
        480,
        320,
        "lowercase")]
    public void KnownUsbIdsSelectTheCorrectCanvas(
        string instanceId,
        GOverlayDeviceModel expectedModel,
        int expectedWidth,
        int expectedHeight,
        string expectedSerial)
    {
        var identified = GOverlayDeviceCatalog.TryIdentify(
            instanceId,
            out var device);

        Assert.True(identified);
        Assert.NotNull(device);
        Assert.Equal(expectedModel, device.Specification.Model);
        Assert.Equal(expectedWidth, device.Specification.PixelWidth);
        Assert.Equal(expectedHeight, device.Specification.PixelHeight);
        Assert.Equal(expectedSerial, device.SerialNumber);
        Assert.Null(device.Specification.PublishedPanelRefreshRateHz);
    }

    [Theory]
    [InlineData(@"USB\VID_20A0&PID_41E5\BS0001")]
    [InlineData(@"USB\VID_1234&PID_41ED\NOT-GOVERLAY")]
    [InlineData(@"HID\VID_20A0&PID_41ED\NOT-USB")]
    public void OtherDevicesAreNotMisidentified(string instanceId)
    {
        Assert.False(GOverlayDeviceCatalog.TryIdentify(instanceId, out _));
    }

    [Theory]
    [InlineData("USB\\VID_20A0&PID_41ED&REV_0100", "0100")]
    [InlineData("USB\\VID_20A0&PID_41ED&REV_01AF", "01AF")]
    public void UsbHardwareRevisionIsRecordedWithoutTreatingItAsFirmware(
        string hardwareId,
        string expectedRevision)
    {
        var identified = GOverlayDeviceCatalog.TryIdentify(
            @"USB\VID_20A0&PID_41ED\LCDSZ",
            [hardwareId],
            out var device);

        Assert.True(identified);
        Assert.Equal(expectedRevision, device!.UsbHardwareRevision);
    }

    [Fact]
    public void CatalogRecordsPublishedClassicDisplaySpecifications()
    {
        Assert.Equal(262_144, GOverlayDeviceCatalog.LcdSysInfo28.ColourCount);
        Assert.Equal(2 * 1024 * 1024, GOverlayDeviceCatalog.LcdSysInfo28.AssetStorageBytes);
        Assert.Equal(262_144, GOverlayDeviceCatalog.LcdSysInfo35.ColourCount);
        Assert.Equal(8 * 1024 * 1024, GOverlayDeviceCatalog.LcdSysInfo35.AssetStorageBytes);
    }

    [Fact]
    public void WindowsDetectorCanEnumeratePresentHardware()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var devices = WindowsGOverlayDeviceDetector.FindConnected();

        Assert.All(
            devices,
            device => Assert.Contains(device.Specification, GOverlayDeviceCatalog.All));
        if (Environment.GetEnvironmentVariable(
                "DESKTOP_SHRINE_REQUIRE_GOVERLAY") == "1")
            Assert.NotEmpty(devices);
    }
}
