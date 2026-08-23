using System.Globalization;
using DesktopShrine.Abstractions;
using Microsoft.Extensions.Configuration;

namespace DesktopShrine.Plugin.TaskbarControls;

internal sealed record TaskbarControlsSettings
{
    public required string QuickAccess { get; init; }
    public required TaskbarSliderSettings Brightness { get; init; }
    public required TaskbarSliderSettings Gamma { get; init; }
    public required TaskbarDeviceSettings AudioDevice { get; init; }

    public static TaskbarControlsSettings FromConfiguration(
        IConfiguration configuration) => new()
    {
        QuickAccess = configuration["QuickAccess"]
            ?? "blinkstick.brightness;blinkstick.gamma;audio.device",
        Brightness = TaskbarSliderSettings.FromConfiguration(
            configuration.GetSection("BlinkStickBrightness"),
            id: "blinkstick-brightness",
            displayName: "BlinkStick brightness",
            targetPluginId: "blinkstick-bar",
            settingPath: "Brightness",
            minimum: 0f,
            maximum: 1f,
            step: 0.01f,
            defaultValue: 1f,
            mapping: TaskbarSliderMapping.Logarithmic,
            logarithmicRange: 100d,
            format: TaskbarSliderValueFormat.Percent),
        Gamma = TaskbarSliderSettings.FromConfiguration(
            configuration.GetSection("BlinkStickGamma"),
            id: "blinkstick-gamma",
            displayName: "BlinkStick gamma",
            targetPluginId: "blinkstick-bar",
            settingPath: "Gamma",
            minimum: 0.1f,
            maximum: 4f,
            step: 0.1f,
            defaultValue: 2.2f,
            mapping: TaskbarSliderMapping.Linear,
            logarithmicRange: 100d,
            format: TaskbarSliderValueFormat.DecimalOne),
        AudioDevice = TaskbarDeviceSettings.FromConfiguration(
            configuration.GetSection("AudioDevice"))
    };

    public static ConfigurationValidationResult Validate(
        TaskbarControlsSettings value)
    {
        var errors = new List<string>();
        ValidateSlider(value.Brightness, errors);
        ValidateSlider(value.Gamma, errors);
        ValidateTarget(
            value.AudioDevice.DisplayName,
            value.AudioDevice.TargetPluginId,
            value.AudioDevice.SettingPath,
            "Audio device",
            errors);
        if (string.IsNullOrWhiteSpace(value.AudioDevice.CaptureModeSettingPath)
            || value.AudioDevice.CaptureModeSettingPath
                .Split(':').Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("Audio device CaptureModeSettingPath is invalid.");
        }

        return errors.Count == 0
            ? ConfigurationValidationResult.Success
            : ConfigurationValidationResult.Failure([.. errors]);
    }

    private static void ValidateSlider(
        TaskbarSliderSettings value,
        List<string> errors)
    {
        ValidateTarget(
            value.DisplayName,
            value.TargetPluginId,
            value.SettingPath,
            value.DisplayName,
            errors);
        if (!float.IsFinite(value.Minimum)
            || !float.IsFinite(value.Maximum)
            || !float.IsFinite(value.Step)
            || value.Maximum <= value.Minimum
            || value.Step <= 0
            || value.Step > value.Maximum - value.Minimum)
        {
            errors.Add($"{value.DisplayName} slider range is invalid.");
        }
        if (value.DefaultValue < value.Minimum
            || value.DefaultValue > value.Maximum)
            errors.Add($"{value.DisplayName} default value is outside its range.");
        if (value.Mapping == TaskbarSliderMapping.Logarithmic
            && (!double.IsFinite(value.LogarithmicRange)
                || value.LogarithmicRange <= 1d))
        {
            errors.Add($"{value.DisplayName} logarithmic range must be greater than 1.");
        }
    }

    private static void ValidateTarget(
        string displayName,
        string pluginId,
        string settingPath,
        string description,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            errors.Add($"{description} DisplayName is required.");
        if (string.IsNullOrWhiteSpace(pluginId)
            || pluginId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || pluginId.Contains(Path.DirectorySeparatorChar)
            || pluginId.Contains(Path.AltDirectorySeparatorChar))
            errors.Add($"{description} PluginId is invalid.");
        if (string.IsNullOrWhiteSpace(settingPath)
            || settingPath.Split(':').Any(string.IsNullOrWhiteSpace))
            errors.Add($"{description} SettingPath is invalid.");
    }
}

internal sealed record TaskbarSliderSettings(
    string Id,
    string DisplayName,
    string TargetPluginId,
    string SettingPath,
    float Minimum,
    float Maximum,
    float Step,
    float DefaultValue,
    TaskbarSliderMapping Mapping,
    double LogarithmicRange,
    TaskbarSliderValueFormat Format)
{
    public static TaskbarSliderSettings FromConfiguration(
        IConfiguration section,
        string id,
        string displayName,
        string targetPluginId,
        string settingPath,
        float minimum,
        float maximum,
        float step,
        float defaultValue,
        TaskbarSliderMapping mapping,
        double logarithmicRange,
        TaskbarSliderValueFormat format) => new(
            id,
            section["DisplayName"] ?? displayName,
            section["PluginId"] ?? targetPluginId,
            section["SettingPath"] ?? settingPath,
            ReadFloat(section, "Minimum", minimum),
            ReadFloat(section, "Maximum", maximum),
            ReadFloat(section, "SliderStep", step),
            ReadFloat(section, "DefaultValue", defaultValue),
            Enum.TryParse<TaskbarSliderMapping>(
                section["Mapping"],
                ignoreCase: true,
                out var parsedMapping)
                ? parsedMapping
                : mapping,
            ReadDouble(section, "LogarithmicRange", logarithmicRange),
            format);

    private static float ReadFloat(
        IConfiguration section,
        string key,
        float fallback) => float.TryParse(
            section[key],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : fallback;

    private static double ReadDouble(
        IConfiguration section,
        string key,
        double fallback) => double.TryParse(
            section[key],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : fallback;
}

internal sealed record TaskbarDeviceSettings(
    string Id,
    string DisplayName,
    string TargetPluginId,
    string SettingPath,
    string CaptureModeSettingPath)
{
    public static TaskbarDeviceSettings FromConfiguration(
        IConfiguration section) => new(
            "audio-device",
            section["DisplayName"] ?? "Audio source",
            section["PluginId"] ?? "audio-collector",
            section["SettingPath"] ?? "Device",
            section["CaptureModeSettingPath"] ?? "CaptureMode");
}
