using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DesktopShrine.Plugin.GOverlay;

public enum GOverlayDeviceModel
{
    LcdSysInfo28,
    LcdSysInfo35
}

public sealed record GOverlayDeviceSpecification
{
    public required GOverlayDeviceModel Model { get; init; }
    public required string DisplayName { get; init; }
    public required int UsbVendorId { get; init; }
    public required int UsbProductId { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required int ColourCount { get; init; }
    public required int AssetStorageBytes { get; init; }
    public required string UsbConnector { get; init; }

    // GOverlay publishes an application update recommendation, not a panel Hz.
    public double? PublishedPanelRefreshRateHz { get; init; }
}

public sealed record GOverlayDevice(
    GOverlayDeviceSpecification Specification,
    string InstanceId,
    string? SerialNumber,
    string? UsbHardwareRevision = null);

public static class GOverlayDeviceCatalog
{
    public const int VendorId = 0x20A0;

    public static GOverlayDeviceSpecification LcdSysInfo28 { get; } = new()
    {
        Model = GOverlayDeviceModel.LcdSysInfo28,
        DisplayName = "LCDSysInfo for GOverlay 2.8-inch",
        UsbVendorId = VendorId,
        UsbProductId = 0x41EC,
        PixelWidth = 320,
        PixelHeight = 240,
        ColourCount = 262_144,
        AssetStorageBytes = 2 * 1024 * 1024,
        UsbConnector = "USB 1.1 mini-B"
    };

    public static GOverlayDeviceSpecification LcdSysInfo35 { get; } = new()
    {
        Model = GOverlayDeviceModel.LcdSysInfo35,
        DisplayName = "LCDSysInfo for GOverlay 3.5-inch (2.0)",
        UsbVendorId = VendorId,
        UsbProductId = 0x41ED,
        PixelWidth = 480,
        PixelHeight = 320,
        ColourCount = 262_144,
        AssetStorageBytes = 8 * 1024 * 1024,
        UsbConnector = "micro-USB"
    };

    public static IReadOnlyList<GOverlayDeviceSpecification> All { get; } =
        [LcdSysInfo28, LcdSysInfo35];

    public static bool TryIdentify(
        string deviceInstanceId,
        [NotNullWhen(true)] out GOverlayDevice? device)
        => TryIdentify(deviceInstanceId, Array.Empty<string>(), out device);

    public static bool TryIdentify(
        string deviceInstanceId,
        IEnumerable<string> hardwareIds,
        [NotNullWhen(true)] out GOverlayDevice? device)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceInstanceId);

        var parts = deviceInstanceId.Split('\\', 3);
        if (parts.Length < 2 || !parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase))
        {
            device = null;
            return false;
        }

        var hardwareId = parts[1];
        var specification = All.FirstOrDefault(candidate =>
            hardwareId.Contains(
                $"VID_{candidate.UsbVendorId:X4}",
                StringComparison.OrdinalIgnoreCase)
            && hardwareId.Contains(
                $"PID_{candidate.UsbProductId:X4}",
                StringComparison.OrdinalIgnoreCase));
        if (specification is null)
        {
            device = null;
            return false;
        }

        var serial = parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2])
            ? parts[2]
            : null;
        var usbHardwareRevision = hardwareIds
            .Prepend(hardwareId)
            .Select(ParseUsbHardwareRevision)
            .FirstOrDefault(value => value is not null);
        device = new(
            specification,
            deviceInstanceId,
            serial,
            usbHardwareRevision);
        return true;
    }

    private static string? ParseUsbHardwareRevision(string value)
    {
        const string marker = "REV_";
        var markerIndex = value.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0 || value.Length < markerIndex + marker.Length + 4)
            return null;

        var revision = value.Substring(markerIndex + marker.Length, 4);
        return revision.All(character => Uri.IsHexDigit(character))
            ? revision
            : null;
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsGOverlayDeviceDetector
{
    public static IReadOnlyList<GOverlayDevice> FindConnected()
    {
        var devices = new List<GOverlayDevice>();
        foreach (var present in WindowsPresentDeviceEnumerator.Enumerate())
            if (GOverlayDeviceCatalog.TryIdentify(
                    present.InstanceId,
                    present.HardwareIds,
                    out var device))
                devices.Add(device);

        return devices;
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsPresentDeviceEnumerator
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const uint SpdrpHardwareId = 0x00000001;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static IEnumerable<PresentDevice> Enumerate()
    {
        var deviceSet = SetupDiGetClassDevs(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfAllClasses);
        if (deviceSet == InvalidHandleValue)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            for (uint index = 0; ; index++)
            {
                var deviceInfo = new SpDevInfoData
                {
                    Size = (uint)Marshal.SizeOf<SpDevInfoData>()
                };
                if (!SetupDiEnumDeviceInfo(deviceSet, index, ref deviceInfo))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                        yield break;
                    throw new Win32Exception(error);
                }

                yield return new(
                    GetInstanceId(deviceSet, ref deviceInfo),
                    GetHardwareIds(deviceSet, ref deviceInfo));
            }
        }
        finally
        {
            if (!SetupDiDestroyDeviceInfoList(deviceSet))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static string[] GetHardwareIds(
        IntPtr deviceSet,
        ref SpDevInfoData deviceInfo)
    {
        _ = SetupDiGetDeviceRegistryProperty(
            deviceSet,
            ref deviceInfo,
            SpdrpHardwareId,
            out _,
            null,
            0,
            out var requiredSize);
        if (requiredSize == 0)
            return Array.Empty<string>();

        var buffer = new byte[requiredSize];
        if (!SetupDiGetDeviceRegistryProperty(
                deviceSet,
                ref deviceInfo,
                SpdrpHardwareId,
                out _,
                buffer,
                requiredSize,
                out _))
            return Array.Empty<string>();

        return Encoding.Unicode.GetString(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string GetInstanceId(
        IntPtr deviceSet,
        ref SpDevInfoData deviceInfo)
    {
        _ = SetupDiGetDeviceInstanceId(
            deviceSet,
            ref deviceInfo,
            null,
            0,
            out var requiredSize);
        var error = Marshal.GetLastWin32Error();
        if (requiredSize == 0 || error != ErrorInsufficientBuffer)
            throw new Win32Exception(error);

        var result = new StringBuilder((int)requiredSize);
        if (!SetupDiGetDeviceInstanceId(
                deviceSet,
                ref deviceInfo,
                result,
                requiredSize,
                out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public UIntPtr Reserved;
    }

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetClassDevsW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevInfoData deviceInfoData);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceInstanceIdW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder? deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(
        IntPtr deviceInfoSet);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceRegistryPropertyW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    internal sealed record PresentDevice(
        string InstanceId,
        string[] HardwareIds);
}
