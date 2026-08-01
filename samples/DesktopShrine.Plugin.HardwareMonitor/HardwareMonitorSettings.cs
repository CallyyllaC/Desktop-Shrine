using Microsoft.Extensions.Configuration;

namespace DesktopShrine.Plugin.HardwareMonitor;

internal sealed record HardwareMonitorSettings
{
    public const int RequiredPollIntervalMilliseconds = 1_000;

    public int PollIntervalMilliseconds { get; init; } =
        RequiredPollIntervalMilliseconds;
    public bool LogEverySample { get; init; }
    public bool LogSensorInventory { get; init; }

    public static HardwareMonitorSettings FromConfiguration(
        IConfiguration configuration)
    {
        var settings = new HardwareMonitorSettings
        {
            PollIntervalMilliseconds = ReadInteger(
                configuration,
                "PollIntervalMilliseconds",
                RequiredPollIntervalMilliseconds),
            LogEverySample = ReadBoolean(
                configuration,
                "LogEverySample",
                false),
            LogSensorInventory = ReadBoolean(
                configuration,
                "LogSensorInventory",
                false)
        };

        if (settings.PollIntervalMilliseconds
            != RequiredPollIntervalMilliseconds)
            throw new InvalidOperationException(
                "PollIntervalMilliseconds must be 1000.");
        return settings;
    }

    private static int ReadInteger(
        IConfiguration configuration,
        string key,
        int fallback) =>
        int.TryParse(configuration[key], out var value) ? value : fallback;

    private static bool ReadBoolean(
        IConfiguration configuration,
        string key,
        bool fallback) =>
        bool.TryParse(configuration[key], out var value) ? value : fallback;
}
