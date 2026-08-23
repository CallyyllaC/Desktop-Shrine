using DesktopShrine.Contracts.Hardware;

namespace DesktopShrine.Plugin.BlinkStickBar;

internal readonly record struct RgbColour(byte Red, byte Green, byte Blue);

internal sealed class HardwareWaveRenderer(BlinkStickBarSettings settings)
{
    private readonly SideState gpu = new(settings);
    private readonly SideState cpu = new(settings);
    private DateTimeOffset? lastTelemetryAt;

    public void Update(HardwareMonitorState state)
    {
        lastTelemetryAt = state.CapturedAt;
        if (!state.IsAvailable)
        {
            gpu.UpdateTargets(null, null, null);
            cpu.UpdateTargets(null, null, null);
            return;
        }

        var graphics = state.GraphicsProcessors
            .OrderByDescending(value =>
                value.Type == GraphicsProcessorType.Discrete)
            .ThenBy(value => value.Id)
            .FirstOrDefault();
        gpu.UpdateTargets(
            NormalizePercentage(graphics?.UsagePercent),
            NormalizePower(
                graphics?.TotalBoardPowerWatts ?? graphics?.PowerWatts,
                settings.GpuPowerReferenceWatts),
            Finite(graphics?.HotspotTemperatureCelsius
                ?? graphics?.TemperatureCelsius));

        var cpuPowerReference = state.System.CpuPowerLimitWatts is > 0
            ? state.System.CpuPowerLimitWatts.Value
            : settings.CpuPowerReferenceWatts;
        cpu.UpdateTargets(
            NormalizePercentage(state.System.CpuUsagePercent),
            NormalizePower(state.System.CpuPowerWatts, cpuPowerReference),
            Finite(state.System.CpuTemperatureCelsius));
    }

    public byte[] Render(TimeSpan elapsed, DateTimeOffset now)
    {
        var seconds = (float)Math.Clamp(elapsed.TotalSeconds, 0, 0.25);
        var fresh = lastTelemetryAt.HasValue
            && now - lastTelemetryAt.Value <= settings.TelemetryStaleAfter;
        var half = settings.LedCount / 2;
        var pixels = new Pixel[settings.LedCount];

        RenderSide(
            gpu,
            pixels,
            firstPixel: half - 1,
            direction: -1,
            half,
            seconds,
            now,
            fresh,
            settings.GpuWarmCelsius,
            settings.GpuHotCelsius,
            settings.GpuCriticalCelsius);
        RenderSide(
            cpu,
            pixels,
            firstPixel: half,
            direction: 1,
            half,
            seconds,
            now,
            fresh,
            settings.CpuWarmCelsius,
            settings.CpuHotCelsius,
            settings.CpuCriticalCelsius);

        return Encode(pixels);
    }

    private void RenderSide(
        SideState side,
        Pixel[] pixels,
        int firstPixel,
        int direction,
        int length,
        float elapsedSeconds,
        DateTimeOffset now,
        bool telemetryFresh,
        float warmCelsius,
        float hotCelsius,
        float criticalCelsius)
    {
        side.Advance(settings, elapsedSeconds, telemetryFresh);
        var intensity = Math.Clamp(
            side.Load + (side.Impulse * settings.ImpulseGain),
            0,
            1);
        var speed = Lerp(
            settings.IdleWaveSpeedPixelsPerSecond,
            settings.MaximumWaveSpeedPixelsPerSecond,
            intensity);
        var spacing = Lerp(
            Math.Max(settings.IdleWaveSpacingPixels, length),
            settings.MinimumWaveSpacingPixels,
            intensity);
        side.Phase = PositiveModulo(
            side.Phase + (speed * elapsedSeconds),
            spacing);

        var crestWidth = Lerp(
            settings.IdleCrestWidthPixels,
            settings.ActiveCrestWidthPixels,
            intensity);
        var maximumTrail = Math.Max(
            settings.MinimumTrailPixels,
            length * settings.MaximumTrailFraction);
        var trailLength = Lerp(
            settings.MinimumTrailPixels,
            maximumTrail,
            side.Power);
        var thermal = Thermal(
            side.Temperature,
            now,
            warmCelsius,
            hotCelsius,
            criticalCelsius);

        for (var distance = 0; distance < length; distance++)
        {
            var ahead = PositiveModulo(distance - side.Phase, spacing);
            var behind = PositiveModulo(side.Phase - distance, spacing);
            var crestDistance = Math.Min(ahead, behind);
            var crest = MathF.Exp(
                -0.5f
                * MathF.Pow(
                    crestDistance / Math.Max(crestWidth, 0.01f),
                    2));
            var trail = behind <= trailLength
                ? MathF.Exp(
                    -behind / Math.Max(trailLength * 0.55f, 0.1f))
                : 0;
            var shape = Math.Max(
                crest,
                trail * Lerp(0.2f, 0.75f, side.Power));
            var idleLevel = settings.IdleBrightness
                * (0.2f + (0.8f * crest));
            var workloadLevel = Math.Clamp(
                (side.Load * shape)
                + (side.Impulse * crest * settings.ImpulseGain),
                0,
                1);
            var level = idleLevel
                + (MathF.Pow(workloadLevel, settings.HardwareGamma)
                    * (1 - settings.IdleBrightness));
            level = Math.Clamp(
                Math.Max(level, thermal.MinimumLevel),
                0,
                1);

            var whiteIntensity = Math.Clamp(
                (intensity - settings.WhiteCrestThreshold)
                / Math.Max(1 - settings.WhiteCrestThreshold, 0.01f),
                0,
                1);
            var white = level
                * crest
                * whiteIntensity
                * settings.WhiteCrestStrength
                * (1 - (thermal.WarningStrength * 0.8f));
            var colourLevel = level * (1 - (white * 0.35f));
            pixels[firstPixel + (distance * direction)] = new(
                thermal.Colour.Red / 255f * colourLevel,
                thermal.Colour.Green / 255f * colourLevel,
                thermal.Colour.Blue / 255f * colourLevel,
                white);
        }
    }

    private ThermalState Thermal(
        float? temperature,
        DateTimeOffset now,
        float warm,
        float hot,
        float critical)
    {
        if (!temperature.HasValue)
            return new(settings.CoolColour, 0, 0);

        var value = temperature.Value;
        if (value < warm)
        {
            var progress = Normalize(
                value,
                settings.ThermalBaselineCelsius,
                warm);
            return new(
                Mix(settings.CoolColour, settings.WarmColour, progress),
                0,
                0);
        }
        if (value < hot)
        {
            return new(
                Mix(
                    settings.WarmColour,
                    settings.HotColour,
                    Normalize(value, warm, hot)),
                0,
                0);
        }
        if (value < critical)
        {
            var warning = Normalize(value, hot, critical);
            return new(
                Mix(settings.HotColour, settings.CriticalColour, warning),
                Lerp(0.12f, 0.32f, warning),
                warning);
        }

        var seconds = now.ToUnixTimeMilliseconds() / 1000d;
        var flash = 0.5f + (0.5f * (float)Math.Sin(
            seconds * Math.PI * 2 * settings.CriticalFlashHz));
        return new(
            settings.CriticalColour,
            Lerp(0.48f, 0.9f, flash),
            1);
    }

    private static byte[] Encode(IReadOnlyList<Pixel> pixels)
    {
        var output = new byte[pixels.Count * 4];
        for (var index = 0; index < pixels.Count; index++)
        {
            var pixel = pixels[index];
            var offset = index * 4;
            output[offset] = ToByte(pixel.Green);
            output[offset + 1] = ToByte(pixel.Red);
            output[offset + 2] = ToByte(pixel.Blue);
            output[offset + 3] = ToByte(pixel.White);
        }
        return output;
    }

    private static float? NormalizePercentage(double? value) =>
        Finite(value) is { } finite
            ? Math.Clamp((float)(finite / 100), 0, 1)
            : null;

    private static float? NormalizePower(double? value, double reference) =>
        Finite(value) is { } finite && reference > 0
            ? Math.Clamp((float)(finite / reference), 0, 1)
            : null;

    private static double? Finite(double? value) =>
        value.HasValue && double.IsFinite(value.Value)
            ? value
            : null;

    private static float Normalize(float value, float minimum, float maximum) =>
        Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);

    private static float Lerp(float first, float second, float amount) =>
        first + ((second - first) * Math.Clamp(amount, 0, 1));

    private static RgbColour Mix(
        RgbColour first,
        RgbColour second,
        float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new(
            (byte)Math.Round(Lerp(first.Red, second.Red, amount)),
            (byte)Math.Round(Lerp(first.Green, second.Green, amount)),
            (byte)Math.Round(Lerp(first.Blue, second.Blue, amount)));
    }

    private static float PositiveModulo(float value, float modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static byte ToByte(float value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private readonly record struct Pixel(
        float Red,
        float Green,
        float Blue,
        float White);

    private readonly record struct ThermalState(
        RgbColour Colour,
        float MinimumLevel,
        float WarningStrength);

    private sealed class SideState(BlinkStickBarSettings settings)
    {
        private float? targetLoad;
        private float? targetPower;
        private float? targetTemperature;
        private float? previousTargetLoad;

        public float Load { get; private set; }
        public float Power { get; private set; }
        public float? Temperature { get; private set; }
        public float Impulse { get; private set; }
        public float Phase { get; set; }

        public void UpdateTargets(
            float? load,
            float? power,
            double? temperature)
        {
            if (load.HasValue && previousTargetLoad.HasValue)
            {
                var increase = load.Value - previousTargetLoad.Value;
                if (increase > settings.ImpulseThreshold)
                {
                    Impulse = Math.Max(
                        Impulse,
                        Math.Clamp(
                            (increase - settings.ImpulseThreshold)
                            / Math.Max(1 - settings.ImpulseThreshold, 0.01f),
                            0,
                            1));
                }
            }
            previousTargetLoad = load;
            targetLoad = load;
            targetPower = power;
            targetTemperature = temperature.HasValue
                ? (float)temperature.Value
                : null;
        }

        public void Advance(
            BlinkStickBarSettings value,
            float elapsedSeconds,
            bool telemetryFresh)
        {
            Load = Smooth(
                Load,
                telemetryFresh ? targetLoad ?? 0 : 0,
                elapsedSeconds,
                value.LoadSmoothingSeconds);
            Power = Smooth(
                Power,
                telemetryFresh ? targetPower ?? 0 : 0,
                elapsedSeconds,
                value.PowerSmoothingSeconds);
            if (telemetryFresh && targetTemperature.HasValue)
            {
                Temperature = Temperature.HasValue
                    ? Smooth(
                        Temperature.Value,
                        targetTemperature.Value,
                        elapsedSeconds,
                        value.TemperatureSmoothingSeconds)
                    : targetTemperature.Value;
            }
            else
            {
                Temperature = null;
            }
            Impulse *= MathF.Exp(
                -elapsedSeconds / value.ImpulseDecaySeconds);
        }

        private static float Smooth(
            float current,
            float target,
            float elapsedSeconds,
            float responseSeconds)
        {
            if (elapsedSeconds <= 0)
                return current;
            var amount = 1 - MathF.Exp(-elapsedSeconds / responseSeconds);
            return current + ((target - current) * amount);
        }
    }
}
