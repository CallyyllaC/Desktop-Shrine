namespace DesktopShrine.Plugin.BlinkStickBar;

/// <summary>
/// Applies user gamma, then user brightness, then the electrical output limit
/// to every RGBW frame immediately before it is sent to the BlinkStick.
/// </summary>
internal sealed class LedOutputTransform(BlinkStickBarSettings settings)
{
    public byte[] Apply(ReadOnlySpan<byte> effectFrame)
    {
        if (effectFrame.Length % 4 != 0)
            throw new ArgumentException(
                "An RGBW frame must contain four bytes per pixel.",
                nameof(effectFrame));

        var output = new byte[effectFrame.Length];
        for (var index = 0; index < output.Length; index++)
        {
            output[index] = (byte)Math.Round(
                ApplyNormalized(effectFrame[index] / 255f) * 255);
        }
        return output;
    }

    internal float ApplyNormalized(float channel)
    {
        var shaped = MathF.Pow(
            Math.Clamp(channel, 0, 1),
            settings.Gamma);
        var brightnessScaled = shaped
            * Math.Clamp(settings.Brightness, 0, 1);
        var limited = brightnessScaled * settings.HardwareOutputLimit;
        return Math.Clamp(limited, 0, 1);
    }
}
