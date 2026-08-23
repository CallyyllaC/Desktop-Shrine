using DesktopShrine.Abstractions;

namespace DesktopShrine.Plugin.TaskbarControls;

internal static class DesktopShrineProductInfo
{
    public const string VersionDisplay = "2.0 Alpha";
}

internal sealed record DesktopShrineApplicationPaths(
    string InstallationDirectory,
    string DataDirectory,
    string ConfigurationDirectory,
    string LogsDirectory,
    string PluginDirectory);

internal sealed record DesktopShrineSettingsDefinition(
    IReadOnlyList<PluginSettingsGroup> Groups,
    Func<PluginSettingDefinition, object> ReadValue,
    Func<PluginSettingDefinition, IReadOnlyList<TaskbarChoice>> ReadChoices,
    Func<PluginSettingDefinition, object, CancellationToken, ValueTask> WriteValue,
    Func<IReadOnlyList<string>> ReadQuickAccess,
    Func<IReadOnlyList<string>, CancellationToken, ValueTask> WriteQuickAccess,
    DesktopShrineApplicationPaths Paths,
    Action<ApplicationShutdownKind> ShutdownRequested);
