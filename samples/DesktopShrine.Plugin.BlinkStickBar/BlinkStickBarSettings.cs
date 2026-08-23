using DesktopShrine.Abstractions;
using Microsoft.Extensions.Configuration;

namespace DesktopShrine.Plugin.BlinkStickBar;

internal sealed record BlinkStickBarSettings
{
    public const int MaximumRgbwPixelCount = 48;

    public required int LedCount { get; init; }
    public required int DataChannel { get; init; }
    public required double UsbCurrentMa { get; init; }
    public required double PixelMaximumMa { get; init; }
    public required bool ExternalPower { get; init; }
    public required float Brightness { get; init; }
    public required double AnimationFramesPerSecond { get; init; }
    public required TimeSpan ReconnectInterval { get; init; }
    public required TimeSpan TelemetryStaleAfter { get; init; }
    public required float Attack { get; init; }
    public required float Release { get; init; }
    public required float PeakRise { get; init; }
    public required float PeakDecay { get; init; }
    public required float Gamma { get; init; }
    public required float HardwareGamma { get; init; }
    public required float OnsetMix { get; init; }
    public required float Epsilon { get; init; }
    public required float MinimumFrequencyHz { get; init; }
    public required float MaximumFrequencyHz { get; init; }
    public required float SplitFrequencyHz { get; init; }
    public required float PivotFrequencyHz { get; init; }
    public required float TiltPower { get; init; }
    public required float HighBandPercentile { get; init; }
    public required RgbColour MonochromeColour { get; init; }
    public required float IdleBrightness { get; init; }
    public required float IdleWaveSpeedPixelsPerSecond { get; init; }
    public required float MaximumWaveSpeedPixelsPerSecond { get; init; }
    public required float IdleWaveSpacingPixels { get; init; }
    public required float MinimumWaveSpacingPixels { get; init; }
    public required float IdleCrestWidthPixels { get; init; }
    public required float ActiveCrestWidthPixels { get; init; }
    public required float MinimumTrailPixels { get; init; }
    public required float MaximumTrailFraction { get; init; }
    public required float LoadSmoothingSeconds { get; init; }
    public required float PowerSmoothingSeconds { get; init; }
    public required float TemperatureSmoothingSeconds { get; init; }
    public required float ImpulseThreshold { get; init; }
    public required float ImpulseGain { get; init; }
    public required float ImpulseDecaySeconds { get; init; }
    public required float WhiteCrestThreshold { get; init; }
    public required float WhiteCrestStrength { get; init; }
    public required float GpuPowerReferenceWatts { get; init; }
    public required float CpuPowerReferenceWatts { get; init; }
    public required float ThermalBaselineCelsius { get; init; }
    public required float GpuWarmCelsius { get; init; }
    public required float GpuHotCelsius { get; init; }
    public required float GpuCriticalCelsius { get; init; }
    public required float CpuWarmCelsius { get; init; }
    public required float CpuHotCelsius { get; init; }
    public required float CpuCriticalCelsius { get; init; }
    public required float CriticalFlashHz { get; init; }
    public required RgbColour CoolColour { get; init; }
    public required RgbColour WarmColour { get; init; }
    public required RgbColour HotColour { get; init; }
    public required RgbColour CriticalColour { get; init; }

    public TimeSpan AnimationInterval =>
        TimeSpan.FromSeconds(1 / AnimationFramesPerSecond);

    public float EffectiveBrightness
    {
        get
        {
            var powerLimit = ExternalPower
                ? 1d
                : UsbCurrentMa / (LedCount * PixelMaximumMa);
            return Brightness * (float)Math.Clamp(powerLimit, 0.01d, 1d);
        }
    }

    public float HardwareOutputLimit => ExternalPower
        ? 1f
        : (float)Math.Clamp(
            UsbCurrentMa / (LedCount * PixelMaximumMa),
            0.01d,
            1d);

    public static BlinkStickBarSettings FromConfiguration(
        IConfiguration configuration) => new()
    {
        LedCount = ReadInteger(configuration, "LedCount", 48),
        DataChannel = ReadInteger(configuration, "DataChannel", 0),
        UsbCurrentMa = ReadDouble(configuration, "UsbCurrentMa", 400),
        PixelMaximumMa = ReadDouble(configuration, "PixelMaximumMa", 50),
        ExternalPower = ReadBoolean(configuration, "ExternalPower", false),
        Brightness = ReadFloat(configuration, "Brightness", 0.9f),
        AnimationFramesPerSecond = ReadDouble(
            configuration,
            "AnimationFramesPerSecond",
            20),
        ReconnectInterval = TimeSpan.FromSeconds(ReadDouble(
            configuration,
            "ReconnectIntervalSeconds",
            2)),
        TelemetryStaleAfter = TimeSpan.FromSeconds(ReadDouble(
            configuration,
            "TelemetryStaleAfterSeconds",
            4)),
        Attack = ReadFloat(configuration, "Attack", 0.65f),
        Release = ReadFloat(configuration, "Release", 0.9f),
        PeakRise = ReadFloat(configuration, "PeakRise", 0.35f),
        PeakDecay = ReadFloat(configuration, "PeakDecay", 0.995f),
        Gamma = ReadFloat(configuration, "Gamma", 2.2f),
        HardwareGamma = ReadFloat(
            configuration,
            "HardwareGamma",
            2.2f),
        OnsetMix = ReadFloat(configuration, "OnsetMix", 0.45f),
        Epsilon = ReadFloat(configuration, "Epsilon", 0.000001f),
        MinimumFrequencyHz = ReadFloat(
            configuration,
            "MinimumFrequencyHz",
            45),
        MaximumFrequencyHz = ReadFloat(
            configuration,
            "MaximumFrequencyHz",
            16000),
        SplitFrequencyHz = ReadFloat(
            configuration,
            "SplitFrequencyHz",
            250),
        PivotFrequencyHz = ReadFloat(
            configuration,
            "PivotFrequencyHz",
            1000),
        TiltPower = ReadFloat(configuration, "TiltPower", 0.3f),
        HighBandPercentile = ReadFloat(
            configuration,
            "HighBandPercentile",
            0.85f),
        MonochromeColour = ReadColour(
            configuration["MonochromeColour"] ?? "#FFFFFF"),
        IdleBrightness = ReadFloat(configuration, "IdleBrightness", 0.045f),
        IdleWaveSpeedPixelsPerSecond = ReadFloat(
            configuration,
            "IdleWaveSpeedPixelsPerSecond",
            0.35f),
        MaximumWaveSpeedPixelsPerSecond = ReadFloat(
            configuration,
            "MaximumWaveSpeedPixelsPerSecond",
            7),
        IdleWaveSpacingPixels = ReadFloat(
            configuration,
            "IdleWaveSpacingPixels",
            12),
        MinimumWaveSpacingPixels = ReadFloat(
            configuration,
            "MinimumWaveSpacingPixels",
            3),
        IdleCrestWidthPixels = ReadFloat(
            configuration,
            "IdleCrestWidthPixels",
            2.2f),
        ActiveCrestWidthPixels = ReadFloat(
            configuration,
            "ActiveCrestWidthPixels",
            0.75f),
        MinimumTrailPixels = ReadFloat(
            configuration,
            "MinimumTrailPixels",
            1.25f),
        MaximumTrailFraction = ReadFloat(
            configuration,
            "MaximumTrailFraction",
            0.65f),
        LoadSmoothingSeconds = ReadFloat(
            configuration,
            "LoadSmoothingSeconds",
            0.3f),
        PowerSmoothingSeconds = ReadFloat(
            configuration,
            "PowerSmoothingSeconds",
            0.8f),
        TemperatureSmoothingSeconds = ReadFloat(
            configuration,
            "TemperatureSmoothingSeconds",
            1.5f),
        ImpulseThreshold = ReadFloat(
            configuration,
            "ImpulseThreshold",
            0.08f),
        ImpulseGain = ReadFloat(configuration, "ImpulseGain", 0.9f),
        ImpulseDecaySeconds = ReadFloat(
            configuration,
            "ImpulseDecaySeconds",
            0.75f),
        WhiteCrestThreshold = ReadFloat(
            configuration,
            "WhiteCrestThreshold",
            0.62f),
        WhiteCrestStrength = ReadFloat(
            configuration,
            "WhiteCrestStrength",
            0.85f),
        GpuPowerReferenceWatts = ReadFloat(
            configuration,
            "GpuPowerReferenceWatts",
            300),
        CpuPowerReferenceWatts = ReadFloat(
            configuration,
            "CpuPowerReferenceWatts",
            150),
        ThermalBaselineCelsius = ReadFloat(
            configuration,
            "ThermalBaselineCelsius",
            30),
        GpuWarmCelsius = ReadFloat(configuration, "GpuWarmCelsius", 65),
        GpuHotCelsius = ReadFloat(configuration, "GpuHotCelsius", 85),
        GpuCriticalCelsius = ReadFloat(
            configuration,
            "GpuCriticalCelsius",
            100),
        CpuWarmCelsius = ReadFloat(configuration, "CpuWarmCelsius", 60),
        CpuHotCelsius = ReadFloat(configuration, "CpuHotCelsius", 80),
        CpuCriticalCelsius = ReadFloat(
            configuration,
            "CpuCriticalCelsius",
            95),
        CriticalFlashHz = ReadFloat(configuration, "CriticalFlashHz", 2),
        CoolColour = ReadColour(configuration["CoolColour"] ?? "#20A8FF"),
        WarmColour = ReadColour(configuration["WarmColour"] ?? "#FFC247"),
        HotColour = ReadColour(configuration["HotColour"] ?? "#FF4D32"),
        CriticalColour = ReadColour(
            configuration["CriticalColour"] ?? "#FF001A")
    };

    public static ConfigurationValidationResult Validate(
        BlinkStickBarSettings value)
    {
        var errors = new List<string>();
        if (value.LedCount is < 2 or > MaximumRgbwPixelCount
            || value.LedCount % 2 != 0)
        {
            errors.Add(
                $"LedCount must be an even number between 2 and {MaximumRgbwPixelCount} for equal RGBW halves.");
        }
        if (value.DataChannel is < 0 or > 2)
            errors.Add("DataChannel must be 0, 1, or 2.");
        if (value.UsbCurrentMa <= 0 || value.PixelMaximumMa <= 0)
            errors.Add("UsbCurrentMa and PixelMaximumMa must be greater than zero.");
        if (!IsUnit(value.Brightness) || !IsUnit(value.IdleBrightness))
            errors.Add("Brightness and IdleBrightness must be between 0 and 1.");
        if (value.AnimationFramesPerSecond is < 1 or > 60)
            errors.Add("AnimationFramesPerSecond must be between 1 and 60.");
        if (value.ReconnectInterval <= TimeSpan.Zero
            || value.TelemetryStaleAfter <= TimeSpan.Zero)
            errors.Add("Reconnect and telemetry stale intervals must be positive.");
        if (!IsUnit(value.Attack)
            || !IsUnit(value.Release)
            || !IsUnit(value.PeakRise)
            || !IsUnit(value.PeakDecay)
            || value.PeakDecay == 0
            || value.Gamma <= 0
            || value.HardwareGamma <= 0
            || value.OnsetMix < 0
            || value.Epsilon <= 0)
            errors.Add("Audio envelope and gain settings are invalid.");
        if (value.MinimumFrequencyHz <= 0
            || value.MaximumFrequencyHz <= value.MinimumFrequencyHz
            || value.SplitFrequencyHz < value.MinimumFrequencyHz
            || value.SplitFrequencyHz > value.MaximumFrequencyHz
            || value.PivotFrequencyHz <= 0
            || !IsUnit(value.HighBandPercentile))
            errors.Add("Audio frequency settings are invalid.");
        if (value.IdleWaveSpeedPixelsPerSecond < 0
            || value.MaximumWaveSpeedPixelsPerSecond
                < value.IdleWaveSpeedPixelsPerSecond)
            errors.Add("Wave speed settings are invalid.");
        if (value.MinimumWaveSpacingPixels <= 0
            || value.IdleWaveSpacingPixels < value.MinimumWaveSpacingPixels)
            errors.Add("Wave spacing settings are invalid.");
        if (value.ActiveCrestWidthPixels <= 0
            || value.IdleCrestWidthPixels < value.ActiveCrestWidthPixels
            || value.MinimumTrailPixels < 0
            || !IsUnit(value.MaximumTrailFraction))
            errors.Add("Wave width and trail settings are invalid.");
        if (value.LoadSmoothingSeconds <= 0
            || value.PowerSmoothingSeconds <= 0
            || value.TemperatureSmoothingSeconds <= 0
            || value.ImpulseDecaySeconds <= 0)
            errors.Add("Smoothing and impulse decay durations must be positive.");
        if (!IsUnit(value.ImpulseThreshold)
            || value.ImpulseGain < 0
            || !IsUnit(value.WhiteCrestThreshold)
            || !IsUnit(value.WhiteCrestStrength))
            errors.Add("Impulse and RGBW crest settings are invalid.");
        if (value.GpuPowerReferenceWatts <= 0
            || value.CpuPowerReferenceWatts <= 0)
            errors.Add("Power reference values must be positive.");
        if (!Ordered(
                value.ThermalBaselineCelsius,
                value.GpuWarmCelsius,
                value.GpuHotCelsius,
                value.GpuCriticalCelsius)
            || !Ordered(
                value.ThermalBaselineCelsius,
                value.CpuWarmCelsius,
                value.CpuHotCelsius,
                value.CpuCriticalCelsius)
            || value.CriticalFlashHz <= 0)
            errors.Add("Thermal thresholds must increase from baseline through critical.");
        return errors.Count == 0
            ? ConfigurationValidationResult.Success
            : ConfigurationValidationResult.Failure([.. errors]);
    }

    private static bool IsUnit(float value) => value is >= 0 and <= 1;

    private static bool Ordered(
        float first,
        float second,
        float third,
        float fourth) =>
        first < second && second < third && third < fourth;

    private static int ReadInteger(
        IConfiguration configuration,
        string key,
        int fallback) =>
        int.TryParse(configuration[key], out var value) ? value : fallback;

    private static float ReadFloat(
        IConfiguration configuration,
        string key,
        float fallback) =>
        float.TryParse(
            configuration[key],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
        && float.IsFinite(value)
            ? value
            : fallback;

    private static double ReadDouble(
        IConfiguration configuration,
        string key,
        double fallback) =>
        double.TryParse(
            configuration[key],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
        && double.IsFinite(value)
            ? value
            : fallback;

    private static bool ReadBoolean(
        IConfiguration configuration,
        string key,
        bool fallback) =>
        bool.TryParse(configuration[key], out var value) ? value : fallback;

    private static RgbColour ReadColour(string value)
    {
        var hex = value.Trim().TrimStart('#');
        if (hex.Length != 6
            || !byte.TryParse(
                hex[..2],
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var red)
            || !byte.TryParse(
                hex[2..4],
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var green)
            || !byte.TryParse(
                hex[4..],
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var blue))
        {
            throw new InvalidOperationException(
                "Hardware visualiser colours must be six-digit RGB hex values.");
        }
        return new(red, green, blue);
    }
}
