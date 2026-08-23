using DesktopShrine.Abstractions;

namespace DesktopShrine.Plugin.TaskbarControls;

internal enum TaskbarSliderMapping
{
    Linear,
    Logarithmic
}

internal enum TaskbarSliderValueFormat
{
    Percent,
    DecimalOne
}

internal abstract record TaskbarControlDefinition(
    string Id,
    string DisplayName,
    string TargetPluginId,
    string SettingPath);

internal sealed record TaskbarSliderDefinition(
    string Id,
    string DisplayName,
    string TargetPluginId,
    string SettingPath,
    float Minimum,
    float Maximum,
    float Step,
    TaskbarSliderMapping Mapping,
    double LogarithmicRange,
    TaskbarSliderValueFormat Format) : TaskbarControlDefinition(
        Id,
        DisplayName,
        TargetPluginId,
        SettingPath)
{
    public float ValueFromPosition(int position, int maximumPosition)
    {
        var unit = Mapping == TaskbarSliderMapping.Logarithmic
            ? LogarithmicBrightnessScale.FromSliderPosition(
                position,
                maximumPosition,
                LogarithmicRange)
            : Math.Clamp(position, 0, maximumPosition)
                / (float)maximumPosition;
        return Quantise(Minimum + unit * (Maximum - Minimum));
    }

    public int PositionFromValue(float value, int maximumPosition)
    {
        var unit = (Math.Clamp(value, Minimum, Maximum) - Minimum)
            / (Maximum - Minimum);
        return Mapping == TaskbarSliderMapping.Logarithmic
            ? LogarithmicBrightnessScale.ToSliderPosition(
                unit,
                maximumPosition,
                LogarithmicRange)
            : (int)Math.Round(
                unit * maximumPosition,
                MidpointRounding.AwayFromZero);
    }

    public float Quantise(float value)
    {
        var clamped = Math.Clamp(
            float.IsFinite(value) ? value : Minimum,
            Minimum,
            Maximum);
        var steps = MathF.Round(
            (clamped - Minimum) / Step,
            MidpointRounding.AwayFromZero);
        return Math.Clamp(Minimum + steps * Step, Minimum, Maximum);
    }

    public string FormatValue(float value) => Format switch
    {
        TaskbarSliderValueFormat.Percent => $"{value:P0}",
        TaskbarSliderValueFormat.DecimalOne => value.ToString("0.0"),
        _ => value.ToString(System.Globalization.CultureInfo.CurrentCulture)
    };
}

internal sealed record TaskbarChoice(string Value, string DisplayName);

internal sealed record TaskbarChoiceDefinition(
    string Id,
    string DisplayName,
    string TargetPluginId,
    string SettingPath,
    Func<IReadOnlyList<TaskbarChoice>> GetChoices) : TaskbarControlDefinition(
        Id,
        DisplayName,
        TargetPluginId,
        SettingPath);

internal sealed record TaskbarToggleDefinition(
    string Id,
    string DisplayName,
    string TargetPluginId,
    string SettingPath) : TaskbarControlDefinition(
        Id,
        DisplayName,
        TargetPluginId,
        SettingPath);

internal sealed record TaskbarMenuDefinition(
    IReadOnlyList<TaskbarControlDefinition> Controls,
    IReadOnlyList<TaskbarCommandDefinition> Commands);

internal sealed record TaskbarCommandDefinition(
    string Id,
    string DisplayName,
    TaskbarCommandKind Kind);

internal enum TaskbarCommandKind
{
    OpenSettings,
    Restart,
    Exit
}

internal sealed record TaskbarControlChange(
    TaskbarControlDefinition Control,
    object Value);
