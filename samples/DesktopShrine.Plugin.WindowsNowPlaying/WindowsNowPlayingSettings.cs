using Microsoft.Extensions.Configuration;

namespace DesktopShrine.Plugin.WindowsNowPlaying;

internal sealed record WindowsNowPlayingSettings
{
    public bool IncludeArtwork { get; init; } = true;
    public ulong MaximumArtworkBytes { get; init; } = 16 * 1024 * 1024;
    public bool PublishTimelineUpdates { get; init; } = true;
    public int PausedTimeoutSeconds { get; init; } = 300;

    public TimeSpan PausedTimeout =>
        TimeSpan.FromSeconds(PausedTimeoutSeconds);

    public static WindowsNowPlayingSettings FromConfiguration(
        IConfiguration configuration)
    {
        var settings = new WindowsNowPlayingSettings
        {
            IncludeArtwork = ReadBoolean(
                configuration,
                "IncludeArtwork",
                true),
            MaximumArtworkBytes = ReadUnsignedLong(
                configuration,
                "MaximumArtworkBytes",
                16 * 1024 * 1024),
            PublishTimelineUpdates = ReadBoolean(
                configuration,
                "PublishTimelineUpdates",
                true),
            PausedTimeoutSeconds = ReadInteger(
                configuration,
                "PausedTimeoutSeconds",
                300)
        };

        if (settings.MaximumArtworkBytes is < 1_024 or > 256 * 1024 * 1024)
            throw new InvalidOperationException(
                "MaximumArtworkBytes must be between 1024 and 268435456.");
        if (settings.PausedTimeoutSeconds is < 0 or > 86_400)
            throw new InvalidOperationException(
                "PausedTimeoutSeconds must be between 0 and 86400.");
        return settings;
    }

    private static bool ReadBoolean(
        IConfiguration configuration,
        string key,
        bool fallback) =>
        bool.TryParse(configuration[key], out var value) ? value : fallback;

    private static ulong ReadUnsignedLong(
        IConfiguration configuration,
        string key,
        ulong fallback) =>
        ulong.TryParse(configuration[key], out var value) ? value : fallback;

    private static int ReadInteger(
        IConfiguration configuration,
        string key,
        int fallback) =>
        int.TryParse(configuration[key], out var value) ? value : fallback;
}
