using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopShrine.Plugin.GOverlay.LegacySdk;

internal sealed class GOverlayUsbConnectionMonitor : IDisposable
{
    private const string LcdSysInfo35Id = "VID_20A0&PID_41ED";
    private readonly object gate = new();
    private readonly Timer timer;
    private bool? present;
    private long generation;
    private DateTime changedAtUtc;

    public GOverlayUsbConnectionMonitor()
    {
        Poll(null);
        timer = new Timer(Poll, null, 250, 250);
    }

    public GOverlayUsbConnectionSnapshot Snapshot
    {
        get
        {
            lock (gate)
                return new(
                    present,
                    generation,
                    changedAtUtc);
        }
    }

    public void Dispose() => timer.Dispose();

    private void Poll(object? state)
    {
        _ = state;
        bool current;
        try
        {
            current = IsPresent();
        }
        catch (Win32Exception)
        {
            // A failed observation must not manufacture a disconnect. The next
            // successful poll can still detect the actual transition.
            return;
        }

        lock (gate)
        {
            if (present == current)
                return;
            present = current;
            generation++;
            changedAtUtc = DateTime.UtcNow;
        }
    }

    private static bool IsPresent()
    {
        var deviceSet = SetupDiGetClassDevs(
            IntPtr.Zero,
            "USB",
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
                        return false;
                    throw new Win32Exception(error);
                }

                var instanceId = GetInstanceId(deviceSet, ref deviceInfo);
                if (instanceId.IndexOf(
                        LcdSysInfo35Id,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }
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

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private static readonly IntPtr InvalidHandleValue = new(-1);

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
}

internal sealed class GOverlayUsbConnectionSnapshot(
    bool? isPresent,
    long generation,
    DateTime changedAtUtc)
{
    public bool? IsPresent { get; } = isPresent;
    public long Generation { get; } = generation;
    public DateTime ChangedAtUtc { get; } = changedAtUtc;
}
