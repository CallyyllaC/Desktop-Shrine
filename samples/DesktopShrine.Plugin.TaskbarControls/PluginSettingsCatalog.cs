using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopShrine.Plugin.TaskbarControls;

internal enum PluginSettingControlType
{
    Slider,
    Toggle,
    Choice,
    Number,
    Text,
    Path
}

internal sealed record PluginSettingChoice(string Value, string DisplayName);

internal sealed record PluginSettingDefinition
{
    public required string Id { get; init; }
    public required string SettingPath { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public string Category { get; init; } = "General";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PluginSettingControlType ControlType { get; init; }
    public string DefaultValue { get; init; } = "";
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public double? Step { get; init; }
    public string? Mapping { get; init; }
    public string? Format { get; init; }
    public bool RequiresRestart { get; init; }
    public string? RestartReason { get; init; }
    public bool CanAddToQuickAccess { get; init; }
    public string? ChoiceProvider { get; init; }
    public IReadOnlyList<PluginSettingChoice> Choices { get; init; } = [];
    [JsonIgnore]
    public string PluginId { get; init; } = "";
    [JsonIgnore]
    public string PluginDisplayName { get; init; } = "";
}

internal sealed record PluginSettingsGroup(
    string PluginId,
    string DisplayName,
    string? Description,
    IReadOnlyList<PluginSettingDefinition> Settings);

internal sealed record PluginSettingsDocument
{
    public required string PluginId { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<PluginSettingDefinition> Settings { get; init; } = [];
}

internal static class PluginSettingsCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<PluginSettingsGroup> Discover(string pluginDirectory)
    {
        if (!Directory.Exists(pluginDirectory))
            return [];
        var groups = new List<PluginSettingsGroup>();
        foreach (var path in Directory.EnumerateFiles(
                     pluginDirectory,
                     "settings.json",
                     SearchOption.AllDirectories))
        {
            try
            {
                var document = JsonSerializer.Deserialize<PluginSettingsDocument>(
                    File.ReadAllText(path), JsonOptions);
                if (document is null
                    || string.IsNullOrWhiteSpace(document.PluginId)
                    || string.IsNullOrWhiteSpace(document.DisplayName))
                    continue;
                var settings = document.Settings
                    .Where(IsValid)
                    .Select(setting => setting with
                    {
                        PluginId = document.PluginId,
                        PluginDisplayName = document.DisplayName
                    })
                    .ToArray();
                if (settings.Length > 0)
                    groups.Add(new(document.PluginId, document.DisplayName,
                        document.Description, settings));
            }
            catch (JsonException)
            {
                // A plugin with invalid optional UI metadata must not prevent
                // the tray or the rest of Desktop Shrine from starting.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return groups
            .OrderBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> ParseQuickAccess(string? value) =>
        (value ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(5)
        .ToArray();

    public static string SerialiseQuickAccess(IEnumerable<string> values) =>
        string.Join(';', values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5));

    public static object ParseValue(PluginSettingDefinition setting, string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? setting.DefaultValue : value;
        if (setting.ControlType == PluginSettingControlType.Path
            && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                return Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(text));
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException
                    or PathTooLongException)
            {
                return text;
            }
        }
        return setting.ControlType switch
        {
            PluginSettingControlType.Toggle when bool.TryParse(text, out var flag) => flag,
            PluginSettingControlType.Slider or PluginSettingControlType.Number
                when double.TryParse(text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var number) => number,
            _ => text ?? ""
        };
    }

    private static bool IsValid(PluginSettingDefinition setting) =>
        !string.IsNullOrWhiteSpace(setting.Id)
        && !string.IsNullOrWhiteSpace(setting.SettingPath)
        && !string.IsNullOrWhiteSpace(setting.DisplayName)
        && (setting.ControlType is not PluginSettingControlType.Slider
            || setting.Minimum is not null
            && setting.Maximum is not null
            && setting.Step is > 0
            && setting.Maximum > setting.Minimum);
}
