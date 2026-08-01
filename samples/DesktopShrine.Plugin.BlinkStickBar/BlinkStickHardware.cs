using HidSharp;
using System.ComponentModel;

namespace DesktopShrine.Plugin.BlinkStickBar;

internal interface IBlinkStickHardware : IDisposable
{
    bool IsConnected { get; }
    bool Connect();
    void Send(byte channel, byte[] grbwFrame);
    void TurnOff(byte channel, int byteCount);
}

internal sealed class BlinkStickHardware : IBlinkStickHardware
{
    private const int VendorId = 0x20A0;
    private const int ProductId = 0x41E5;
    private HidStream? stream;

    public bool IsConnected => stream is not null;

    public bool Connect()
    {
        DisposeDevice();
        var device = DeviceList.Local
            .GetHidDevices(VendorId, ProductId)
            .FirstOrDefault();
        if (device is null || !device.TryOpen(out var candidate))
            return false;

        try
        {
            SetFeature(candidate, BlinkStickProtocol.CreateModeReport());
            stream = candidate;
            return true;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    public void Send(byte channel, byte[] grbwFrame)
    {
        if (stream is null)
            throw new InvalidOperationException("No BlinkStick is connected.");
        SetFeature(stream, BlinkStickProtocol.CreateIndexedFrameReport(channel, grbwFrame));
    }

    public void TurnOff(byte channel, int byteCount)
    {
        if (stream is not null)
            Send(channel, new byte[byteCount]);
    }

    public void Dispose()
    {
        DisposeDevice();
        GC.SuppressFinalize(this);
    }

    private void DisposeDevice()
    {
        if (stream is null)
            return;
        stream.Dispose();
        stream = null;
    }

    private static void SetFeature(HidStream target, byte[] report)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                target.SetFeature(report);
                return;
            }
            catch (IOException exception)
                when (exception.InnerException is Win32Exception { NativeErrorCode: 0 })
            {
                // BlinkStick's V-USB firmware can complete a feature report
                // while Windows returns false without setting an error.
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
        }
    }
}

internal static class BlinkStickProtocol
{
    private const int MaximumFrameBytes = 64 * 3;

    public static byte[] CreateModeReport() => [4, 2];

    public static byte[] CreateIndexedFrameReport(byte channel, byte[] frame)
    {
        if (frame.Length > MaximumFrameBytes)
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                $"A BlinkStick indexed frame cannot exceed {MaximumFrameBytes} bytes.");

        var (reportId, frameCapacity) = SelectReport(frame.Length);
        var report = new byte[frameCapacity + 2];
        report[0] = reportId;
        report[1] = channel;
        frame.CopyTo(report, 2);
        return report;
    }

    private static (byte ReportId, int FrameCapacity) SelectReport(int byteCount) =>
        byteCount switch
        {
            <= 8 * 3 => (6, 8 * 3),
            <= 16 * 3 => (7, 16 * 3),
            <= 32 * 3 => (8, 32 * 3),
            _ => (9, MaximumFrameBytes)
        };
}
