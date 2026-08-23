namespace DesktopShrine.Plugin.TaskbarControls;

internal static class LogarithmicBrightnessScale
{
    public static float FromSliderPosition(
        int position,
        int maximum,
        double logarithmicRange)
    {
        Validate(maximum, logarithmicRange);
        var normalised = Math.Clamp(position, 0, maximum) / (double)maximum;
        return (float)((Math.Pow(logarithmicRange, normalised) - 1d)
            / (logarithmicRange - 1d));
    }

    public static int ToSliderPosition(
        float brightness,
        int maximum,
        double logarithmicRange)
    {
        Validate(maximum, logarithmicRange);
        var clamped = Math.Clamp(
            float.IsFinite(brightness) ? brightness : 0f,
            0f,
            1f);
        var normalised = Math.Log(1d + clamped * (logarithmicRange - 1d))
            / Math.Log(logarithmicRange);
        return (int)Math.Round(
            normalised * maximum,
            MidpointRounding.AwayFromZero);
    }

    public static float Quantise(float brightness, int stepPercent)
    {
        if (stepPercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(stepPercent));
        var percent = Math.Clamp(
            float.IsFinite(brightness) ? brightness : 0f,
            0f,
            1f) * 100f;
        var quantisedPercent = MathF.Round(
            percent / stepPercent,
            MidpointRounding.AwayFromZero) * stepPercent;
        return Math.Clamp(quantisedPercent / 100f, 0f, 1f);
    }

    private static void Validate(int maximum, double logarithmicRange)
    {
        if (maximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        if (!double.IsFinite(logarithmicRange) || logarithmicRange <= 1d)
            throw new ArgumentOutOfRangeException(nameof(logarithmicRange));
    }
}
