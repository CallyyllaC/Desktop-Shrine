using Microsoft.Extensions.Configuration;

namespace DesktopShrine.Plugin.ConsoleDisplay;

internal sealed record ConsoleDisplaySettings
{
    public bool ShowMediaUpdates { get; init; } = true;
    public bool ShowAudioStatus { get; init; } = true;
    public TimeSpan AudioStatusInterval { get; init; } = TimeSpan.FromSeconds(2);
    public int AudioMeterWidth { get; init; } = 16;
    public double AudioMeterFloorDb { get; init; } = -60;
    public float SignalThreshold { get; init; } = 0.0001f;

    public static ConsoleDisplaySettings FromConfiguration(
        IConfiguration configuration)
    {
        var settings = new ConsoleDisplaySettings
        {
            ShowMediaUpdates = ReadBoolean(
                configuration,
                "ShowMediaUpdates",
                true),
            ShowAudioStatus = ReadBoolean(
                configuration,
                "ShowAudioStatus",
                true),
            AudioStatusInterval = TimeSpan.FromSeconds(ReadInteger(
                configuration,
                "AudioStatusIntervalSeconds",
                2)),
            AudioMeterWidth = ReadInteger(
                configuration,
                "AudioMeterWidth",
                16),
            AudioMeterFloorDb = ReadDouble(
                configuration,
                "AudioMeterFloorDb",
                -60),
            SignalThreshold = ReadFloat(
                configuration,
                "SignalThreshold",
                0.0001f)
        };

        if (settings.AudioStatusInterval < TimeSpan.FromSeconds(1)
            || settings.AudioStatusInterval > TimeSpan.FromHours(1))
            throw new InvalidOperationException(
                "AudioStatusIntervalSeconds must be between 1 and 3600.");
        if (settings.AudioMeterWidth is < 4 or > 120)
            throw new InvalidOperationException(
                "AudioMeterWidth must be between 4 and 120.");
        if (settings.AudioMeterFloorDb is < -160 or > -1)
            throw new InvalidOperationException(
                "AudioMeterFloorDb must be between -160 and -1.");
        if (settings.SignalThreshold is < 0 or > 1)
            throw new InvalidOperationException(
                "SignalThreshold must be between 0 and 1.");
        return settings;
    }

    private static bool ReadBoolean(
        IConfiguration configuration,
        string key,
        bool fallback) =>
        bool.TryParse(configuration[key], out var value) ? value : fallback;

    private static int ReadInteger(
        IConfiguration configuration,
        string key,
        int fallback) =>
        int.TryParse(configuration[key], out var value) ? value : fallback;

    private static double ReadDouble(
        IConfiguration configuration,
        string key,
        double fallback) =>
        double.TryParse(configuration[key], out var value)
        && double.IsFinite(value)
            ? value
            : fallback;

    private static float ReadFloat(
        IConfiguration configuration,
        string key,
        float fallback) =>
        float.TryParse(configuration[key], out var value)
        && float.IsFinite(value)
            ? value
            : fallback;
}
