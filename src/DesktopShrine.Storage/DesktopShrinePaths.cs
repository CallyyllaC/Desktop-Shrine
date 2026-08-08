namespace DesktopShrine.Storage;

public sealed class DesktopShrinePaths
{
    public const string CanonicalDirectoryName = "DesktopShrine";
    public const string LegacyDirectoryName = "Desktop Shrine";
    public const string GOverlayTraceEnvironmentVariable =
        "DESKTOP_SHRINE_GOVERLAY_TRACE_PATH";
    public const string GOverlayTraceFileName =
        "goverlay-lcd-command-trace.log";

    public DesktopShrinePaths(string localApplicationDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(localApplicationDataDirectory))
            throw new ArgumentException(
                "A LocalAppData directory is required.",
                nameof(localApplicationDataDirectory));

        LocalApplicationDataDirectory = Path.GetFullPath(
            localApplicationDataDirectory);
        Root = Path.Combine(
            LocalApplicationDataDirectory,
            CanonicalDirectoryName);
        LegacyRoot = Path.Combine(
            LocalApplicationDataDirectory,
            LegacyDirectoryName);
        ConfigurationDirectory = Path.Combine(Root, "configuration");
        PluginConfigurationDirectory = Path.Combine(
            ConfigurationDirectory,
            "plugins");
        OutputProfileFile = Path.Combine(
            ConfigurationDirectory,
            "output-input-profiles.json");
        LogsDirectory = Path.Combine(Root, "logs");
        GOverlayCommandTraceFile = Path.Combine(
            LogsDirectory,
            GOverlayTraceFileName);
        SteamNowPlayingDirectory = Path.Combine(
            Root,
            "steam-now-playing");
        SteamArtworkCacheDirectory = Path.Combine(
            SteamNowPlayingDirectory,
            "artwork");
        SteamMetadataCacheDirectory = Path.Combine(
            SteamNowPlayingDirectory,
            "metadata");
        LegacyMigrationBackupDirectory = Path.Combine(
            Root,
            "LegacyMigrationBackup");
    }

    public static DesktopShrinePaths Current { get; } = new(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData));

    public string LocalApplicationDataDirectory { get; }
    public string Root { get; }
    public string LegacyRoot { get; }
    public string ConfigurationDirectory { get; }
    public string PluginConfigurationDirectory { get; }
    public string OutputProfileFile { get; }
    public string LogsDirectory { get; }
    public string GOverlayCommandTraceFile { get; }
    public string SteamNowPlayingDirectory { get; }
    public string SteamArtworkCacheDirectory { get; }
    public string SteamMetadataCacheDirectory { get; }
    public string LegacyMigrationBackupDirectory { get; }

    public string ResolveGOverlayCommandTraceFile(string? configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? GOverlayCommandTraceFile
            : Environment.ExpandEnvironmentVariables(configuredPath!.Trim());
        return Path.GetFullPath(path);
    }
}
