using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Hardware;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Plugin.HardwareMonitor;

public sealed class HardwareMonitorPlugin : IInputPlugin
{
    private const string PortId = "hardware-monitor";
    private IPluginContext? context;
    private ILogger<HardwareMonitorPlugin>? logger;
    private HardwareMonitorSettings settings = new();
    private CancellationTokenSource? stop;
    private Task? worker;
    private InputActivityState activityState = InputActivityState.Inactive;

    public PluginDescriptor Descriptor { get; } = new()
    {
        Id = "hardware-monitor",
        Name = "Hardware Monitor",
        Version = new(2, 0, 0),
        Description =
            "Publishes CPU, GPU, physical and virtual memory, and CMOS battery telemetry from LibreHardwareMonitor.",
        SupportedPlatforms = ["windows"]
    };

    public IReadOnlyCollection<ProvidedPortDescriptor> ProvidedPorts { get; } =
    [
        new()
        {
            PortId = PortId,
            DisplayName = "Hardware monitor",
            Contract = new(
                "desktop-shrine.hardware.monitor",
                new(1, 0, 0))
        }
    ];

    public DefaultInputPriorityPlacement DefaultPriorityPlacement =>
        DefaultInputPriorityPlacement.Lowest;

    public InputActivityState ActivityState => activityState;

    public event EventHandler<InputActivityStateChangedEventArgs>?
        ActivityStateChanged;

    public ValueTask InitialiseAsync(
        IPluginContext value,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        context = value;
        logger = value.LoggerFactory.CreateLogger<HardwareMonitorPlugin>();
        settings = HardwareMonitorSettings.FromConfiguration(
            value.Configuration);
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Hardware Monitor requires Windows.");
        if (context is null)
            throw new InvalidOperationException(
                "Hardware Monitor has not been initialised.");

        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        worker = Task.Run(
            () => RunAsync(stop.Token),
            CancellationToken.None);
        logger!.LogInformation(
            "Hardware Monitor input started with LibreHardwareMonitor; polling every {PollIntervalMilliseconds} ms",
            settings.PollIntervalMilliseconds);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        if (stop is null)
            return;

        await stop.CancelAsync();
        if (worker is not null)
        {
            try { await worker.WaitAsync(token); }
            catch (OperationCanceledException) { }
        }
        SetActivity(InputActivityState.Inactive);
    }

    public ValueTask DisposeAsync()
    {
        stop?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(CancellationToken token)
    {
        LibreHardwareMonitorTelemetrySource? source = null;
        var capabilitiesLogged = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    source ??= LibreHardwareMonitorTelemetrySource.Open();
                    var snapshot = source.Read();
                    SetActivity(InputActivityState.Active);
                    await context!.Publisher.PublishAsync(
                        PortId,
                        snapshot,
                        token);

                    if (!capabilitiesLogged)
                    {
                        LogCapabilities(snapshot, source);
                        capabilitiesLogged = true;
                    }
                    if (settings.LogEverySample)
                        LogSample(snapshot);
                }
                catch (OperationCanceledException) when (
                    token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    SetActivity(InputActivityState.Inactive);
                    logger!.LogWarning(
                        exception,
                        "Could not read LibreHardwareMonitor telemetry");
                    await PublishUnavailableAsync(exception, token);
                    source?.Dispose();
                    source = null;
                    capabilitiesLogged = false;
                }

                await Task.Delay(
                    settings.PollIntervalMilliseconds,
                    token);
            }
        }
        catch (OperationCanceledException) when (
            token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger!.LogError(
                exception,
                "Hardware Monitor input failed");
            throw;
        }
        finally
        {
            source?.Dispose();
        }
    }

    private async Task PublishUnavailableAsync(
        Exception exception,
        CancellationToken token)
    {
        try
        {
            await context!.Publisher.PublishAsync(
                PortId,
                new HardwareMonitorState
                {
                    IsAvailable = false,
                    CapturedAt = DateTimeOffset.UtcNow,
                    Provider = "LibreHardwareMonitor",
                    Error = exception.Message
                },
                token);
        }
        catch (OperationCanceledException) when (
            token.IsCancellationRequested)
        {
        }
    }

    private void LogCapabilities(
        HardwareMonitorState snapshot,
        LibreHardwareMonitorTelemetrySource source)
    {
        logger!.LogInformation(
            "LibreHardwareMonitor {Version} detected CPU {CpuName} and {GpuCount} GPU(s); system metrics: {SystemMetrics}; {DriverStatus}",
            snapshot.ProviderVersion,
            snapshot.System.ProcessorName ?? "unknown",
            snapshot.GraphicsProcessors.Count,
            Names(snapshot.System.SupportedMetrics),
            source.GetDriverStatus());
        foreach (var gpu in snapshot.GraphicsProcessors)
        {
            logger!.LogInformation(
                "GPU {GpuId}: {GpuName} ({GpuType}, {MemorySizeMb} MB); metrics: {GpuMetrics}",
                gpu.Id,
                gpu.Name,
                gpu.Type,
                gpu.TotalMemoryBytes / (1024 * 1024),
                Names(gpu.SupportedMetrics));
        }
        LogSample(snapshot);
        if (settings.LogSensorInventory)
        {
            foreach (var sensor in source.GetSensorInventory())
            {
                logger!.LogInformation(
                    "LibreHardwareMonitor sensor: {Sensor}",
                    sensor);
            }
        }
    }

    private void LogSample(HardwareMonitorState snapshot)
    {
        logger!.LogInformation(
            "Hardware sample: CPU {CpuUsage:F1}%, {CpuTemperature:F1} C, {CpuPower:F1} W, {CpuClock:F0} MHz; CMOS {CmosVoltage:F2} V; RAM {UsedMemoryMb}/{TotalMemoryMb} MB; virtual {UsedVirtualMb}/{TotalVirtualMb} MB",
            snapshot.System.CpuUsagePercent,
            snapshot.System.CpuTemperatureCelsius,
            snapshot.System.CpuPowerWatts,
            snapshot.System.CpuClockMegahertz,
            snapshot.System.CmosBatteryVoltageVolts,
            Megabytes(snapshot.System.UsedMemoryBytes),
            Megabytes(snapshot.System.TotalMemoryBytes),
            Megabytes(snapshot.System.UsedVirtualMemoryBytes),
            Megabytes(snapshot.System.TotalVirtualMemoryBytes));
        foreach (var gpu in snapshot.GraphicsProcessors)
        {
            logger!.LogInformation(
                "Hardware sample {GpuName}: load {Usage:F1}%, clock {Clock} MHz, VRAM {UsedMemoryMb}/{TotalMemoryMb} MB at {MemoryClock} MHz, edge/hotspot {Temperature:F1}/{Hotspot:F1} C, GPU/board power {Power:F1}/{BoardPower:F1} W, fan {FanSpeed} RPM, voltage {Voltage} mV",
                gpu.Name,
                gpu.UsagePercent,
                gpu.ClockMegahertz,
                Megabytes(gpu.UsedMemoryBytes),
                Megabytes(gpu.TotalMemoryBytes),
                gpu.MemoryClockMegahertz,
                gpu.TemperatureCelsius,
                gpu.HotspotTemperatureCelsius,
                gpu.PowerWatts,
                gpu.TotalBoardPowerWatts,
                gpu.FanSpeedRpm,
                gpu.VoltageMillivolts);
        }
    }

    private static long? Megabytes(long? bytes) =>
        bytes / (1024 * 1024);

    private static string Names(
        IReadOnlyCollection<HardwareMetric> metrics) =>
        metrics.Count == 0
            ? "none"
            : string.Join(", ", metrics);

    private void SetActivity(InputActivityState next)
    {
        var previous = activityState;
        if (previous == next)
            return;

        activityState = next;
        ActivityStateChanged?.Invoke(this, new(previous, next));
    }
}
