using DesktopShrine.Contracts.Audio;
using DesktopShrine.Contracts.Media;

namespace DesktopShrine.Plugin.BlinkStickBar;

internal sealed class AudioSpectrumRenderer
{
    private readonly BlinkStickBarSettings settings;
    private readonly float[] envelope;
    private readonly float[] previousBands;
    private readonly float[] centreFrequencies;
    private RgbColour[] palette;
    private byte[] currentFrame;
    private float lowPeak;
    private float highPeak;

    public AudioSpectrumRenderer(BlinkStickBarSettings settings)
    {
        this.settings = settings;
        envelope = new float[settings.LedCount];
        previousBands = new float[settings.LedCount];
        centreFrequencies = new float[settings.LedCount];
        palette = CreateMonochromePalette(settings.MonochromeColour);
        currentFrame = new byte[settings.LedCount * 4];
        lowPeak = settings.Epsilon;
        highPeak = settings.Epsilon;
    }

    public void Update(AudioSpectrumFrame frame)
    {
        if (frame.Spectrum.Count == 0
            || frame.SampleRate <= 0
            || frame.FftSize <= 0)
            return;

        var bands = ExtractBands(frame);
        var driven = new float[bands.Length];
        for (var index = 0; index < bands.Length; index++)
        {
            var onset = Math.Max(0, bands[index] - previousBands[index]);
            previousBands[index] = bands[index];
            var target = bands[index] + (onset * settings.OnsetMix);
            envelope[index] = target >= envelope[index]
                ? Lerp(envelope[index], target, settings.Attack)
                : (envelope[index] * settings.Release)
                    + (target * (1 - settings.Release));
            driven[index] = envelope[index];
        }

        var lowValues = driven
            .Where((_, index) =>
                centreFrequencies[index] <= settings.SplitFrequencyHz)
            .ToArray();
        var highValues = driven
            .Where((_, index) =>
                centreFrequencies[index] > settings.SplitFrequencyHz)
            .ToArray();
        lowPeak = AdvancePeak(
            lowPeak,
            lowValues.Length == 0 ? 0 : lowValues.Max());
        highPeak = AdvancePeak(
            highPeak,
            Percentile(highValues, settings.HighBandPercentile));

        var output = new byte[settings.LedCount * 4];
        for (var index = 0; index < driven.Length; index++)
        {
            var reference = centreFrequencies[index]
                <= settings.SplitFrequencyHz
                ? lowPeak
                : highPeak;
            var level = Math.Clamp(
                driven[index] / Math.Max(reference, settings.Epsilon),
                0,
                1);
            EncodePixel(
                output,
                index,
                ColourForLevel(palette, level),
                level);
        }
        currentFrame = output;
    }

    public void Update(MediaColourPalette palette)
    {
        this.palette = palette.IsAvailable
            ? CreatePerceivedPalette(
                palette.OutputDark.BaseColour,
                palette.OutputDominant.BaseColour,
                palette.OutputAccent.BaseColour,
                palette.OutputLight.BaseColour)
            : CreateMonochromePalette(settings.MonochromeColour);
    }

    public byte[] Render() => currentFrame;

    private float[] ExtractBands(AudioSpectrumFrame frame)
    {
        var output = new float[settings.LedCount];
        var nyquist = frame.SampleRate / 2f;
        var minimum = Math.Clamp(settings.MinimumFrequencyHz, 1, nyquist);
        var maximum = Math.Clamp(settings.MaximumFrequencyHz, minimum, nyquist);
        var ratio = MathF.Pow(maximum / minimum, 1f / settings.LedCount);

        for (var index = 0; index < output.Length; index++)
        {
            var lower = minimum * MathF.Pow(ratio, index);
            var upper = minimum * MathF.Pow(ratio, index + 1);
            centreFrequencies[index] = MathF.Sqrt(lower * upper);
            var firstBin = Math.Clamp(
                (int)MathF.Floor(lower * frame.FftSize / frame.SampleRate),
                0,
                frame.Spectrum.Count - 1);
            var finalBin = Math.Clamp(
                (int)MathF.Ceiling(upper * frame.FftSize / frame.SampleRate),
                firstBin,
                frame.Spectrum.Count - 1);
            var maximumMagnitude = 0f;
            for (var bin = firstBin; bin <= finalBin; bin++)
                maximumMagnitude = Math.Max(
                    maximumMagnitude,
                    Math.Max(0, frame.Spectrum[bin]));
            var tilt = MathF.Pow(
                Math.Max(centreFrequencies[index], 1)
                    / settings.PivotFrequencyHz,
                settings.TiltPower);
            output[index] = maximumMagnitude * tilt;
        }
        return output;
    }

    private float AdvancePeak(float current, float observed) =>
        observed > current
            ? Lerp(current, observed, settings.PeakRise)
            : Math.Max(settings.Epsilon, current * settings.PeakDecay);

    private void EncodePixel(
        byte[] output,
        int index,
        RgbColour colour,
        float level)
    {
        var offset = index * 4;
        var scale = level;
        if (colour.Red == colour.Green && colour.Green == colour.Blue)
        {
            output[offset + 3] = ToByte(
                scale * (colour.Red / 255f));
            return;
        }

        output[offset] = ToByte(scale * (colour.Green / 255f));
        output[offset + 1] = ToByte(scale * (colour.Red / 255f));
        output[offset + 2] = ToByte(scale * (colour.Blue / 255f));
    }

    internal static RgbColour[] CreatePerceivedPalette(
        BaseColour dark,
        BaseColour dominant,
        BaseColour accent,
        BaseColour light)
    {
        RgbColour[] colours =
        [
            FromBaseColour(dark),
            FromBaseColour(dominant),
            FromBaseColour(accent),
            FromBaseColour(light)
        ];
        return colours
            .OrderBy(PerceivedLuminance)
            .ToArray();
    }

    internal static RgbColour ColourForLevel(
        IReadOnlyList<RgbColour> palette,
        float level)
    {
        if (palette.Count == 0)
            return default;
        if (palette.Count == 1)
            return palette[0];

        var position = Math.Clamp(level, 0, 1) * (palette.Count - 1);
        var lower = Math.Min((int)MathF.Floor(position), palette.Count - 1);
        var upper = Math.Min(lower + 1, palette.Count - 1);
        return InterpolatePerceived(
            palette[lower],
            palette[upper],
            position - lower);
    }

    private static RgbColour[] CreateMonochromePalette(RgbColour colour) =>
        [colour, colour, colour, colour];

    private static RgbColour FromBaseColour(BaseColour value) =>
        new(value.Red, value.Green, value.Blue);

    private static double PerceivedLuminance(RgbColour value) =>
        (0.2126 * ToLinear(value.Red))
        + (0.7152 * ToLinear(value.Green))
        + (0.0722 * ToLinear(value.Blue));

    private static RgbColour InterpolatePerceived(
        RgbColour first,
        RgbColour second,
        float amount) => new(
            InterpolateChannel(first.Red, second.Red, amount),
            InterpolateChannel(first.Green, second.Green, amount),
            InterpolateChannel(first.Blue, second.Blue, amount));

    private static byte InterpolateChannel(
        byte first,
        byte second,
        float amount)
    {
        var linear = Lerp(
            (float)ToLinear(first),
            (float)ToLinear(second),
            amount);
        var srgb = linear <= 0.0031308f
            ? linear * 12.92f
            : (1.055f * MathF.Pow(linear, 1 / 2.4f)) - 0.055f;
        return ToByte(srgb);
    }

    private static double ToLinear(byte value)
    {
        var srgb = value / 255d;
        return srgb <= 0.04045
            ? srgb / 12.92
            : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }

    private static float Percentile(float[] values, float percentile)
    {
        if (values.Length == 0)
            return 0;
        Array.Sort(values);
        var index = (int)Math.Round(
            Math.Clamp(percentile, 0, 1) * (values.Length - 1));
        return values[index];
    }

    private static float Lerp(float first, float second, float amount) =>
        first + ((second - first) * Math.Clamp(amount, 0, 1));

    private static byte ToByte(float value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
