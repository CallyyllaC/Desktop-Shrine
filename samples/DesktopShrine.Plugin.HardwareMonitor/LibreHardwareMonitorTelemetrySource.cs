using System.Globalization;
using DesktopShrine.Contracts.Hardware;
using LibreHardwareMonitor.Hardware;

namespace DesktopShrine.Plugin.HardwareMonitor;

internal sealed class LibreHardwareMonitorTelemetrySource : IDisposable
{
    private const double Gibibyte = 1024d * 1024 * 1024;
    private const double Mebibyte = 1024d * 1024;
    private readonly Computer computer;
    private bool disposed;

    private LibreHardwareMonitorTelemetrySource(Computer computer)
    {
        this.computer = computer;
    }

    public static LibreHardwareMonitorTelemetrySource Open()
    {
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsBatteryEnabled = false,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
            IsStorageEnabled = false
        };
        try
        {
            computer.Open();
            return new(computer);
        }
        catch
        {
            computer.Close();
            throw;
        }
    }

    public HardwareMonitorState Read()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var hardware = computer.Hardware.ToArray();
        foreach (var item in hardware)
            UpdateRecursively(item);

        var cpu = hardware.FirstOrDefault(
            item => item.HardwareType == HardwareType.Cpu);
        var memory = hardware
            .Where(item => item.HardwareType == HardwareType.Memory)
            .ToArray();
        var motherboard = hardware.FirstOrDefault(
            item => item.HardwareType == HardwareType.Motherboard);
        var graphics = hardware
            .Where(item => item.HardwareType is
                HardwareType.GpuAmd or
                HardwareType.GpuIntel or
                HardwareType.GpuNvidia)
            .Select((item, index) => ReadGpu(item, index))
            .ToArray();
        var system = ReadSystem(cpu, memory, motherboard);

        return new()
        {
            IsAvailable = cpu is not null || graphics.Length > 0,
            CapturedAt = DateTimeOffset.UtcNow,
            Provider = "LibreHardwareMonitor",
            ProviderVersion =
                typeof(Computer).Assembly.GetName().Version?.ToString(),
            System = system,
            GraphicsProcessors = graphics,
            Error = cpu is null && graphics.Length == 0
                ? "LibreHardwareMonitor did not expose a CPU or GPU."
                : null
        };
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        computer.Close();
    }

    public IReadOnlyList<string> GetSensorInventory()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = new List<string>();
        foreach (var hardware in computer.Hardware)
            AddInventory(hardware, result);
        return result;
    }

    public string GetDriverStatus()
    {
        var version = LibreHardwareMonitor.PawnIo.PawnIo.Version;
        return string.Concat(
            "PawnIO installed=",
            LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled,
            ", version=",
            version?.ToString() ?? "unavailable");
    }

    private static SystemTelemetry ReadSystem(
        IHardware? cpu,
        IReadOnlyList<IHardware> memory,
        IHardware? motherboard)
    {
        var cpuSensors = Sensors(cpu);
        var physicalMemory = memory.FirstOrDefault(
            item => !item.Name.Contains(
                        "Virtual",
                        StringComparison.OrdinalIgnoreCase)
                    && !item.Identifier.ToString().StartsWith(
                        "/vram",
                        StringComparison.OrdinalIgnoreCase));
        var virtualMemory = memory.FirstOrDefault(
            item => item.Name.Contains(
                        "Virtual",
                        StringComparison.OrdinalIgnoreCase)
                    || item.Identifier.ToString().StartsWith(
                        "/vram",
                        StringComparison.OrdinalIgnoreCase));
        var physicalMemorySensors = Sensors(physicalMemory);
        var virtualMemorySensors = Sensors(virtualMemory);
        var boardSensors = Sensors(motherboard);

        var usage = FirstValue(
            cpuSensors,
            SensorType.Load,
            "CPU Total",
            "CPU Core Max")
            ?? AverageValue(
                cpuSensors,
                SensorType.Load,
                name => name.Contains(
                    "CPU Core",
                    StringComparison.OrdinalIgnoreCase));
        var temperature = FirstPositiveValue(
            cpuSensors,
            SensorType.Temperature,
            "CPU Package",
            "Core (Tctl/Tdie)",
            "CPU (Tctl/Tdie)",
            "Core Average")
            ?? MaximumPositiveValue(
                cpuSensors,
                SensorType.Temperature,
                name => !name.Contains(
                    "Distance",
                    StringComparison.OrdinalIgnoreCase));
        var power = FirstPositiveValue(
            cpuSensors,
            SensorType.Power,
            "CPU Package",
            "Package",
            "CPU PPT",
            "PPT")
            ?? MaximumPositiveValue(cpuSensors, SensorType.Power);
        var clock = FirstPositiveValue(
            cpuSensors,
            SensorType.Clock,
            "CPU Core Average",
            "Core Average",
            "Cores (Average)",
            "Cores (Average Effective)")
            ?? AveragePositiveValue(
                cpuSensors,
                SensorType.Clock,
                name => name.Contains(
                            "Core",
                            StringComparison.OrdinalIgnoreCase)
                        && !name.Contains(
                            "Bus",
                            StringComparison.OrdinalIgnoreCase));

        var usedMemory = DataBytes(
            Find(physicalMemorySensors, SensorType.Data, "Memory Used"));
        var availableMemory = DataBytes(
            Find(physicalMemorySensors, SensorType.Data, "Memory Available"));
        var usedVirtual = DataBytes(
            Find(virtualMemorySensors, SensorType.Data, "Memory Used"));
        var availableVirtual = DataBytes(
            Find(virtualMemorySensors, SensorType.Data, "Memory Available"));
        var cmos = FirstValue(
            boardSensors,
            SensorType.Voltage,
            "CMOS Battery",
            "VBAT",
            "VBat",
            "Battery");

        var metrics = new List<HardwareMetric>();
        Add(metrics, HardwareMetric.CpuUsage, usage);
        Add(metrics, HardwareMetric.CpuTemperature, temperature);
        Add(metrics, HardwareMetric.CpuPower, power);
        Add(metrics, HardwareMetric.CpuClock, clock);
        Add(metrics, HardwareMetric.SystemMemory, usedMemory);
        Add(metrics, HardwareMetric.VirtualMemory, usedVirtual);
        Add(metrics, HardwareMetric.CmosBatteryVoltage, cmos);
        return new()
        {
            ProcessorName = cpu?.Name,
            CpuUsagePercent = usage,
            CpuTemperatureCelsius = temperature,
            CpuPowerWatts = power,
            CpuClockMegahertz = clock,
            UsedMemoryBytes = usedMemory,
            TotalMemoryBytes = Sum(usedMemory, availableMemory),
            UsedVirtualMemoryBytes = usedVirtual,
            TotalVirtualMemoryBytes = Sum(usedVirtual, availableVirtual),
            CmosBatteryVoltageVolts = cmos,
            SupportedMetrics = metrics
        };
    }

    private static GraphicsProcessorTelemetry ReadGpu(
        IHardware gpu,
        int index)
    {
        var sensors = Sensors(gpu);
        var usage = FirstValue(
            sensors,
            SensorType.Load,
            "GPU Core",
            "D3D 3D",
            "GPU Total")
            ?? MaximumValue(sensors, SensorType.Load);
        var clock = FirstValue(
            sensors,
            SensorType.Clock,
            "GPU Core",
            "GPU Clock");
        var memoryClock = FirstValue(
            sensors,
            SensorType.Clock,
            "GPU Memory");
        var temperature = FirstValue(
            sensors,
            SensorType.Temperature,
            "GPU Core",
            "GPU Temperature");
        var hotspot = FirstValue(
            sensors,
            SensorType.Temperature,
            "GPU Hot Spot",
            "GPU Hotspot",
            "Hot Spot");
        var intake = FirstValue(
            sensors,
            SensorType.Temperature,
            "GPU Intake");
        var corePower = FirstValue(
            sensors,
            SensorType.Power,
            "GPU Core");
        var boardPower = FirstValue(
            sensors,
            SensorType.Power,
            "GPU Total",
            "GPU Board Power",
            "GPU Package",
            "GPU PPT");
        var fan = FirstValue(
            sensors,
            SensorType.Fan,
            "GPU Fan",
            "GPU Fan 1")
            ?? MaximumValue(sensors, SensorType.Fan);
        var voltage = FirstValue(
            sensors,
            SensorType.Voltage,
            "GPU Core");
        var usedMemory = SensorBytes(
            FindAny(
                sensors,
                [SensorType.SmallData, SensorType.Data],
                "GPU Memory Used"));
        var totalMemory = SensorBytes(
            FindAny(
                sensors,
                [SensorType.SmallData, SensorType.Data],
                "GPU Memory Total"));

        var metrics = new List<HardwareMetric>();
        Add(metrics, HardwareMetric.GpuUsage, usage);
        Add(metrics, HardwareMetric.GpuClock, clock);
        Add(metrics, HardwareMetric.GpuMemoryClock, memoryClock);
        Add(metrics, HardwareMetric.GpuTemperature, temperature);
        Add(metrics, HardwareMetric.GpuHotspotTemperature, hotspot);
        Add(metrics, HardwareMetric.GpuIntakeTemperature, intake);
        Add(metrics, HardwareMetric.GpuPower, corePower);
        Add(metrics, HardwareMetric.GpuTotalBoardPower, boardPower);
        Add(metrics, HardwareMetric.GpuFanSpeed, fan);
        Add(metrics, HardwareMetric.GpuMemory, usedMemory);
        Add(metrics, HardwareMetric.GpuVoltage, voltage);
        return new()
        {
            Id = index,
            Name = gpu.Name,
            Type = GpuType(gpu),
            TotalMemoryBytes = totalMemory,
            UsagePercent = usage,
            ClockMegahertz = Rounded(clock),
            MemoryClockMegahertz = Rounded(memoryClock),
            TemperatureCelsius = temperature,
            HotspotTemperatureCelsius = hotspot,
            IntakeTemperatureCelsius = intake,
            PowerWatts = corePower,
            TotalBoardPowerWatts = boardPower,
            FanSpeedRpm = Rounded(fan),
            UsedMemoryBytes = usedMemory,
            VoltageMillivolts = Rounded(voltage * 1000),
            SupportedMetrics = metrics
        };
    }

    private static IReadOnlyList<ISensor> Sensors(IHardware? hardware)
    {
        if (hardware is null)
            return [];
        var result = new List<ISensor>();
        CollectSensors(hardware, result);
        return result;
    }

    private static void CollectSensors(
        IHardware hardware,
        ICollection<ISensor> result)
    {
        foreach (var sensor in hardware.Sensors)
            result.Add(sensor);
        foreach (var child in hardware.SubHardware)
            CollectSensors(child, result);
    }

    private static void UpdateRecursively(IHardware hardware)
    {
        hardware.Update();
        foreach (var child in hardware.SubHardware)
            UpdateRecursively(child);
    }

    private static void AddInventory(
        IHardware hardware,
        ICollection<string> result)
    {
        foreach (var sensor in hardware.Sensors)
        {
            result.Add(string.Concat(
                hardware.Name,
                " [",
                hardware.HardwareType,
                "] / ",
                sensor.Name,
                " [",
                sensor.SensorType,
                "] = ",
                sensor.Value?.ToString(
                    "0.####",
                    CultureInfo.InvariantCulture)
                    ?? "null",
                " / ",
                sensor.Identifier));
        }
        foreach (var child in hardware.SubHardware)
            AddInventory(child, result);
    }

    private static double? FirstValue(
        IReadOnlyList<ISensor> sensors,
        SensorType type,
        params string[] preferredNames)
    {
        foreach (var name in preferredNames)
        {
            var exact = sensors.FirstOrDefault(
                sensor => sensor.SensorType == type
                    && string.Equals(
                        sensor.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase));
            var value = Value(exact);
            if (value.HasValue)
                return value;
        }
        foreach (var name in preferredNames)
        {
            var partial = sensors.FirstOrDefault(
                sensor => sensor.SensorType == type
                    && sensor.Name.Contains(
                        name,
                        StringComparison.OrdinalIgnoreCase));
            var value = Value(partial);
            if (value.HasValue)
                return value;
        }
        return null;
    }

    private static ISensor? Find(
        IReadOnlyList<ISensor> sensors,
        SensorType type,
        string name) =>
        sensors.FirstOrDefault(
            sensor => sensor.SensorType == type
                && string.Equals(
                    sensor.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
        ?? sensors.FirstOrDefault(
            sensor => sensor.SensorType == type
                && sensor.Name.Contains(
                    name,
                    StringComparison.OrdinalIgnoreCase));

    private static ISensor? FindAny(
        IReadOnlyList<ISensor> sensors,
        IReadOnlyCollection<SensorType> types,
        string name) =>
        sensors.FirstOrDefault(
            sensor => types.Contains(sensor.SensorType)
                && string.Equals(
                    sensor.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
        ?? sensors.FirstOrDefault(
            sensor => types.Contains(sensor.SensorType)
                && sensor.Name.Contains(
                    name,
                    StringComparison.OrdinalIgnoreCase));

    private static double? AverageValue(
        IReadOnlyList<ISensor> sensors,
        SensorType type,
        Func<string, bool> predicate)
    {
        var values = sensors
            .Where(sensor =>
                sensor.SensorType == type && predicate(sensor.Name))
            .Select(Value)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static double? AveragePositiveValue(
        IReadOnlyList<ISensor> sensors,
        SensorType type,
        Func<string, bool> predicate)
    {
        var value = AverageValue(sensors, type, predicate);
        return value is > 0 ? value : null;
    }

    private static double? MaximumValue(
        IReadOnlyList<ISensor> sensors,
        SensorType type,
        Func<string, bool>? predicate = null)
    {
        var values = sensors
            .Where(sensor =>
                sensor.SensorType == type
                && (predicate is null || predicate(sensor.Name)))
            .Select(Value)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Max();
    }

    private static double? MaximumPositiveValue(
        IReadOnlyList<ISensor> sensors,
        SensorType type,
        Func<string, bool>? predicate = null)
    {
        var values = sensors
            .Where(sensor =>
                sensor.SensorType == type
                && (predicate is null || predicate(sensor.Name)))
            .Select(Value)
            .Where(value => value is > 0)
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Max();
    }

    private static double? FirstPositiveValue(
        IReadOnlyList<ISensor> sensors,
        SensorType type,
        params string[] preferredNames)
    {
        foreach (var name in preferredNames)
        {
            var exact = sensors.FirstOrDefault(
                sensor => sensor.SensorType == type
                    && string.Equals(
                        sensor.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase));
            var value = Value(exact);
            if (value is > 0)
                return value;
        }
        foreach (var name in preferredNames)
        {
            var partial = sensors.FirstOrDefault(
                sensor => sensor.SensorType == type
                    && sensor.Name.Contains(
                        name,
                        StringComparison.OrdinalIgnoreCase));
            var value = Value(partial);
            if (value is > 0)
                return value;
        }
        return null;
    }

    private static double? Value(ISensor? sensor) =>
        sensor?.Value is { } value && float.IsFinite(value)
            ? value
            : null;

    private static long? DataBytes(ISensor? sensor) =>
        Value(sensor) is { } value
            ? checked((long)Math.Round(value * Gibibyte))
            : null;

    private static long? SensorBytes(ISensor? sensor)
    {
        var value = Value(sensor);
        if (!value.HasValue || sensor is null)
            return null;
        var multiplier = sensor.SensorType == SensorType.SmallData
            ? Mebibyte
            : Gibibyte;
        return checked((long)Math.Round(value.Value * multiplier));
    }

    private static long? Sum(long? first, long? second) =>
        first.HasValue && second.HasValue
            ? checked(first.Value + second.Value)
            : null;

    private static int? Rounded(double? value) =>
        value.HasValue && double.IsFinite(value.Value)
            ? checked((int)Math.Round(value.Value))
            : null;

    private static GraphicsProcessorType GpuType(IHardware gpu)
    {
        if (gpu.HardwareType == HardwareType.GpuNvidia)
            return GraphicsProcessorType.Discrete;
        if (gpu.Name.Contains(
                "Radeon Graphics",
                StringComparison.OrdinalIgnoreCase)
            || gpu.Name.Contains(
                "UHD Graphics",
                StringComparison.OrdinalIgnoreCase)
            || gpu.Name.Contains(
                "Iris",
                StringComparison.OrdinalIgnoreCase))
        {
            return GraphicsProcessorType.Integrated;
        }
        return GraphicsProcessorType.Discrete;
    }

    private static void Add(
        ICollection<HardwareMetric> metrics,
        HardwareMetric metric,
        double? value)
    {
        if (value.HasValue)
            metrics.Add(metric);
    }

    private static void Add(
        ICollection<HardwareMetric> metrics,
        HardwareMetric metric,
        long? value)
    {
        if (value.HasValue)
            metrics.Add(metric);
    }
}
