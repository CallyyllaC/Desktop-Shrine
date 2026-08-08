using Microsoft.Extensions.Configuration;
using DesktopShrine.Storage;

namespace DesktopShrine.Plugin.SteamNowPlaying;

internal sealed record SteamNowPlayingSettings
{
    public int PollIntervalMilliseconds { get; init; } = 2_000;
    public bool IncludeArtwork { get; init; }
    public bool DownloadArtwork { get; init; } = true;
    public bool IncludeStoreMetadata { get; init; } = true;
    public bool DownloadStoreMetadata { get; init; } = true;
    public int StoreMetadataCacheHours { get; init; } = 24;
    public long MaximumArtworkBytes { get; init; } = 16 * 1024 * 1024;
    public int ArtworkDownloadTimeoutSeconds { get; init; } = 5;
    public string UnknownGameTitle { get; init; } = "Unknown Steam game";
    public required string ArtworkCacheDirectory { get; init; }
    public required string StoreMetadataCacheDirectory { get; init; }

    public static SteamNowPlayingSettings FromConfiguration(
        IConfiguration configuration)
    {
        var defaultCache =
            DesktopShrinePaths.Current.SteamArtworkCacheDirectory;
        var configuredCache = configuration["ArtworkCacheDirectory"];
        var defaultMetadataCache =
            DesktopShrinePaths.Current.SteamMetadataCacheDirectory;
        var configuredMetadataCache =
            configuration["StoreMetadataCacheDirectory"];

        var settings = new SteamNowPlayingSettings
        {
            PollIntervalMilliseconds = ReadInteger(
                configuration,
                "PollIntervalMilliseconds",
                2_000),
            IncludeArtwork = ReadBoolean(configuration, "IncludeArtwork", false),
            DownloadArtwork = ReadBoolean(configuration, "DownloadArtwork", true),
            IncludeStoreMetadata = ReadBoolean(
                configuration,
                "IncludeStoreMetadata",
                true),
            DownloadStoreMetadata = ReadBoolean(
                configuration,
                "DownloadStoreMetadata",
                true),
            StoreMetadataCacheHours = ReadInteger(
                configuration,
                "StoreMetadataCacheHours",
                24),
            MaximumArtworkBytes = ReadLong(
                configuration,
                "MaximumArtworkBytes",
                16 * 1024 * 1024),
            ArtworkDownloadTimeoutSeconds = ReadInteger(
                configuration,
                "ArtworkDownloadTimeoutSeconds",
                5),
            UnknownGameTitle = string.IsNullOrWhiteSpace(
                configuration["UnknownGameTitle"])
                ? "Unknown Steam game"
                : configuration["UnknownGameTitle"]!.Trim(),
            ArtworkCacheDirectory = Path.GetFullPath(
                string.IsNullOrWhiteSpace(configuredCache)
                    ? defaultCache
                    : Environment.ExpandEnvironmentVariables(configuredCache)),
            StoreMetadataCacheDirectory = Path.GetFullPath(
                string.IsNullOrWhiteSpace(configuredMetadataCache)
                    ? defaultMetadataCache
                    : Environment.ExpandEnvironmentVariables(
                        configuredMetadataCache))
        };

        if (settings.PollIntervalMilliseconds is < 250 or > 60_000)
            throw new InvalidOperationException(
                "PollIntervalMilliseconds must be between 250 and 60000.");
        if (settings.MaximumArtworkBytes is < 1_024 or > 256L * 1024 * 1024)
            throw new InvalidOperationException(
                "MaximumArtworkBytes must be between 1024 and 268435456.");
        if (settings.ArtworkDownloadTimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException(
                "ArtworkDownloadTimeoutSeconds must be between 1 and 60.");
        if (settings.StoreMetadataCacheHours is < 1 or > 720)
            throw new InvalidOperationException(
                "StoreMetadataCacheHours must be between 1 and 720.");

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

    private static long ReadLong(
        IConfiguration configuration,
        string key,
        long fallback) =>
        long.TryParse(configuration[key], out var value) ? value : fallback;
}
