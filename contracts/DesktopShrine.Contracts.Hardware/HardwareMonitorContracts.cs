using DesktopShrine.Abstractions;

[assembly: ShrineContractPackage(
    "desktop-shrine.contracts.hardware",
    "1.0.0")]

namespace DesktopShrine.Contracts.Hardware;

public enum GraphicsProcessorType
{
    Unknown,
    Integrated,
    Discrete
}

public enum HardwareMetric
{
    CpuUsage,
    CpuTemperature,
    CpuPower,
    CpuClock,
    SystemMemory,
    VirtualMemory,
    CmosBatteryVoltage,
    SmartShift,
    FramesPerSecond,
    GpuUsage,
    GpuClock,
    GpuMemoryClock,
    GpuTemperature,
    GpuHotspotTemperature,
    GpuIntakeTemperature,
    GpuPower,
    GpuTotalBoardPower,
    GpuFanSpeed,
    GpuMemory,
    GpuVoltage
}

public sealed record SystemTelemetry
{
    public string? ProcessorName { get; init; }
    public double? CpuUsagePercent { get; init; }
    public double? CpuTemperatureCelsius { get; init; }
    public double? CpuPowerWatts { get; init; }
    public double? CpuPowerLimitWatts { get; init; }
    public double? CpuClockMegahertz { get; init; }
    public double? CpuClockLimitMegahertz { get; init; }
    public long? UsedMemoryBytes { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public long? UsedVirtualMemoryBytes { get; init; }
    public long? TotalVirtualMemoryBytes { get; init; }
    public double? CmosBatteryVoltageVolts { get; init; }
    public int? SmartShift { get; init; }
    public int? FramesPerSecond { get; init; }
    public string? CpuTelemetryError { get; init; }
    public IReadOnlyList<HardwareMetric> SupportedMetrics { get; init; } = [];
}

public sealed record GraphicsProcessorTelemetry
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public GraphicsProcessorType Type { get; init; }
    public bool IsExternal { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public string? MemoryType { get; init; }
    public double? UsagePercent { get; init; }
    public int? ClockMegahertz { get; init; }
    public int? MemoryClockMegahertz { get; init; }
    public double? TemperatureCelsius { get; init; }
    public double? HotspotTemperatureCelsius { get; init; }
    public double? IntakeTemperatureCelsius { get; init; }
    public double? PowerWatts { get; init; }
    public double? TotalBoardPowerWatts { get; init; }
    public int? FanSpeedRpm { get; init; }
    public long? UsedMemoryBytes { get; init; }
    public int? VoltageMillivolts { get; init; }
    public IReadOnlyList<HardwareMetric> SupportedMetrics { get; init; } = [];
}

[ShrineContract(
    "desktop-shrine.hardware.monitor",
    "1.0.0",
    DeliveryKind.State)]
public sealed record HardwareMonitorState : IShrineState
{
    public required bool IsAvailable { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required string Provider { get; init; }
    public string? ProviderVersion { get; init; }
    public SystemTelemetry System { get; init; } = new();
    public IReadOnlyList<GraphicsProcessorTelemetry> GraphicsProcessors { get; init; } = [];
    public string? Error { get; init; }
}
