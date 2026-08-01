using DesktopShrine.Abstractions;

namespace DesktopShrine.Contracts.Media;

public readonly record struct BaseColour(byte Red, byte Green, byte Blue)
{
    public string Hex => $"#{Red:X2}{Green:X2}{Blue:X2}";
}

public enum ColourTemperature { Cool, Neutral, Warm }

public readonly record struct WeightedPaletteSample(BaseColour Colour, double Weight);

public sealed record PaletteStatistics
{
    public double TotalWeight { get; init; }
    public double ChromaticWeight { get; init; }
    public double HueSinSum { get; init; }
    public double HueCosSum { get; init; }
    public double SaturationSum { get; init; }
    public double LuminanceSum { get; init; }
    public double TemperatureWeight { get; init; }
    public double WarmthSum { get; init; }

    public static PaletteStatistics FromSamples(IEnumerable<WeightedPaletteSample> samples)
    {
        var totalWeight = 0d;
        var chromaticWeight = 0d;
        var hueSinSum = 0d;
        var hueCosSum = 0d;
        var saturationSum = 0d;
        var luminanceSum = 0d;
        var temperatureWeight = 0d;
        var warmthSum = 0d;

        foreach (var sample in samples)
        {
            var weight = Math.Max(0, sample.Weight);
            if (weight == 0)
                continue;

            var hsl = ColourCalculations.ToHsl(sample.Colour);
            var hueRadians = hsl.Hue * Math.PI / 180d;
            var hueWeight = weight * hsl.Saturation;

            totalWeight += weight;
            chromaticWeight += hueWeight;
            hueSinSum += Math.Sin(hueRadians) * hueWeight;
            hueCosSum += Math.Cos(hueRadians) * hueWeight;
            saturationSum += hsl.Saturation * weight;
            luminanceSum += hsl.Luminance * weight;

            // Warm colours cluster around orange; cool colours cluster around blue.
            temperatureWeight += hueWeight;
            warmthSum += Math.Cos((hsl.Hue - 40d) * Math.PI / 180d) * hueWeight;
        }

        return new()
        {
            TotalWeight = totalWeight,
            ChromaticWeight = chromaticWeight,
            HueSinSum = hueSinSum,
            HueCosSum = hueCosSum,
            SaturationSum = saturationSum,
            LuminanceSum = luminanceSum,
            TemperatureWeight = temperatureWeight,
            WarmthSum = warmthSum
        };
    }
}

public sealed class PaletteColour
{
    private readonly Lazy<IReadOnlyList<BaseColour>> shades;
    private readonly Lazy<IReadOnlyList<BaseColour>> analogous;
    private readonly Lazy<IReadOnlyList<BaseColour>> splitComplementary;
    private readonly Lazy<IReadOnlyList<BaseColour>> triadic;
    private readonly Lazy<IReadOnlyList<BaseColour>> tetradic;

    public PaletteColour(BaseColour baseColour)
    {
        BaseColour = baseColour;
        shades = new(() => ColourCalculations.Shades(BaseColour));
        analogous = new(() => ColourCalculations.RotateHue(BaseColour, -30, 30));
        splitComplementary = new(() => ColourCalculations.RotateHue(BaseColour, 150, 210));
        triadic = new(() => ColourCalculations.RotateHue(BaseColour, 120, 240));
        tetradic = new(() => ColourCalculations.RotateHue(BaseColour, 90, 180, 270));
    }

    public BaseColour BaseColour { get; }
    public IReadOnlyList<BaseColour> Shades => shades.Value;
    public IReadOnlyList<BaseColour> Analogous => analogous.Value;
    public IReadOnlyList<BaseColour> SplitComplementary => splitComplementary.Value;
    public IReadOnlyList<BaseColour> Triadic => triadic.Value;
    public IReadOnlyList<BaseColour> Tetradic => tetradic.Value;
}

[ShrineContract("desktop-shrine.media.colour-palette", "1.0.0", DeliveryKind.State)]
public sealed class MediaColourPalette : IShrineState
{
    public const double NearBlackMaximumValue = 0.05;
    public const double NearBlackSaturation = 0.78;
    public const double GrayscaleMaximumSaturation = 0.12;
    public const double PaleGrayscaleMinimumValue = 0.60;
    public const double PureBlackGrayValue = 0.035;
    public const double OutputDarkMinimumSaturation = 0.70;
    public const double OutputDarkMinimumValue = 0.10;
    public const double OutputDominantMinimumSaturation = 0.55;
    public const double OutputDominantMinimumValue = 0.28;
    public const double OutputAccentMinimumSaturation = 0.65;
    public const double OutputAccentMinimumValue = 0.48;
    public const double OutputLightMinimumSaturation = 0.30;
    public const double OutputLightMinimumValue = 0.75;

    private readonly Lazy<PaletteAverages> averages;
    private readonly Lazy<PaletteColour> outputDominant;
    private readonly Lazy<PaletteColour> outputAccent;
    private readonly Lazy<PaletteColour> outputDark;
    private readonly Lazy<PaletteColour> outputLight;

    public MediaColourPalette(
        PaletteColour dominant,
        PaletteColour accent,
        PaletteColour dark,
        PaletteColour light,
        PaletteStatistics statistics,
        bool isAvailable = true)
    {
        Dominant = dominant;
        Accent = accent;
        Dark = dark;
        Light = light;
        IsAvailable = isAvailable;
        averages = new(() => CalculateAverages(statistics));
        var referenceHue = FindReferenceHue(
            Accent,
            Dominant,
            Light,
            Dark);
        // Raw palette colours remain truthful to the artwork. Emissive consumers
        // can opt into cached HSV-adjusted variants without changing extraction.
        outputDominant = new(() => IsAvailable
            ? OutputSafe(
                Dominant,
                referenceHue,
                OutputDominantMinimumSaturation,
                OutputDominantMinimumValue)
            : Dominant);
        outputAccent = new(() => IsAvailable
            ? OutputSafe(
                Accent,
                referenceHue,
                OutputAccentMinimumSaturation,
                OutputAccentMinimumValue)
            : Accent);
        outputDark = new(() => IsAvailable
            ? OutputSafe(
                Dark,
                referenceHue,
                OutputDarkMinimumSaturation,
                OutputDarkMinimumValue)
            : Dark);
        outputLight = new(() => IsAvailable
            ? OutputSafe(
                Light,
                referenceHue,
                OutputLightMinimumSaturation,
                OutputLightMinimumValue)
            : Light);
    }

    public static MediaColourPalette Unavailable { get; } = CreateUnavailable();

    public bool IsAvailable { get; }
    public PaletteColour Dominant { get; }
    public PaletteColour Accent { get; }
    public PaletteColour Dark { get; }
    public PaletteColour Light { get; }
    public PaletteColour OutputDominant => outputDominant.Value;
    public PaletteColour OutputAccent => outputAccent.Value;
    public PaletteColour OutputDark => outputDark.Value;
    public PaletteColour OutputLight => outputLight.Value;
    public double AverageHue => averages.Value.Hue;
    public double AverageSaturation => averages.Value.Saturation;
    public double AverageLuminance => averages.Value.Luminance;
    public ColourTemperature ColourTemperature => averages.Value.Temperature;

    private static MediaColourPalette CreateUnavailable()
    {
        var black = new PaletteColour(new(0, 0, 0));
        return new(black, black, black, black, new(), isAvailable: false);
    }

    private static PaletteAverages CalculateAverages(PaletteStatistics statistics)
    {
        var hue = statistics.ChromaticWeight == 0
            ? 0
            : Math.Atan2(statistics.HueSinSum, statistics.HueCosSum) * 180d / Math.PI;
        if (hue < 0)
            hue += 360;

        var saturation = statistics.TotalWeight == 0 ? 0 : statistics.SaturationSum / statistics.TotalWeight;
        var luminance = statistics.TotalWeight == 0 ? 0 : statistics.LuminanceSum / statistics.TotalWeight;
        var warmth = statistics.TemperatureWeight == 0 ? 0 : statistics.WarmthSum / statistics.TemperatureWeight;
        var temperature = warmth switch
        {
            > 0.15 => ColourTemperature.Warm,
            < -0.15 => ColourTemperature.Cool,
            _ => ColourTemperature.Neutral
        };

        return new(hue, saturation, luminance, temperature);
    }

    private static PaletteColour OutputSafe(
        PaletteColour source,
        double? referenceHue,
        double minimumSaturation,
        double minimumValue)
    {
        if (source.BaseColour == new BaseColour(0, 0, 0))
        {
            var gray = (byte)Math.Round(PureBlackGrayValue * 255d);
            return new(new(gray, gray, gray));
        }

        var hsv = ColourCalculations.ToHsv(source.BaseColour);

        // White and pale neutral shades are useful peak colours and should not
        // acquire the artwork hue merely because their saturation is low.
        if (hsv.Saturation <= GrayscaleMaximumSaturation
            && hsv.Value >= PaleGrayscaleMinimumValue)
            return source;

        if (hsv.Value < NearBlackMaximumValue)
        {
            return new(ColourCalculations.FromHsv(hsv with
            {
                Hue = referenceHue ?? hsv.Hue,
                Saturation = referenceHue.HasValue
                    ? NearBlackSaturation
                    : 0,
                Value = Math.Max(hsv.Value, minimumValue)
            }));
        }

        if (hsv.Saturation <= GrayscaleMaximumSaturation)
        {
            return new(ColourCalculations.FromHsv(hsv with
            {
                Hue = referenceHue ?? hsv.Hue,
                Saturation = referenceHue.HasValue
                    ? minimumSaturation
                    : 0,
                Value = Math.Max(hsv.Value, minimumValue)
            }));
        }

        // Existing chromatic colours keep their hue. Only lift channels that
        // would otherwise be too dim or washed out for an emissive display.
        return new(ColourCalculations.FromHsv(hsv with
        {
            Saturation = Math.Max(hsv.Saturation, minimumSaturation),
            Value = Math.Max(hsv.Value, minimumValue)
        }));
    }

    private static double? FindReferenceHue(
        params PaletteColour[] colours)
    {
        foreach (var colour in colours)
        {
            var hsv = ColourCalculations.ToHsv(colour.BaseColour);
            if (hsv.Value >= NearBlackMaximumValue
                && hsv.Saturation > GrayscaleMaximumSaturation)
                return hsv.Hue;
        }

        return null;
    }

    private sealed record PaletteAverages(double Hue, double Saturation, double Luminance, ColourTemperature Temperature);
}

internal static class ColourCalculations
{
    internal readonly record struct Hsl(double Hue, double Saturation, double Luminance);
    internal readonly record struct Hsv(double Hue, double Saturation, double Value);

    public static Hsl ToHsl(BaseColour colour)
    {
        var red = colour.Red / 255d;
        var green = colour.Green / 255d;
        var blue = colour.Blue / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var luminance = (maximum + minimum) / 2d;

        if (delta == 0)
            return new(0, 0, luminance);

        var saturation = delta / (1d - Math.Abs(2d * luminance - 1d));
        var hue = maximum == red
            ? 60d * (((green - blue) / delta) % 6d)
            : maximum == green
                ? 60d * (((blue - red) / delta) + 2d)
                : 60d * (((red - green) / delta) + 4d);

        if (hue < 0)
            hue += 360d;
        return new(hue, saturation, luminance);
    }

    public static IReadOnlyList<BaseColour> Shades(BaseColour colour)
    {
        var hsl = ToHsl(colour);
        return [
            FromHsl(hsl with { Luminance = hsl.Luminance * 0.8 }),
            FromHsl(hsl with { Luminance = hsl.Luminance * 0.6 }),
            FromHsl(hsl with { Luminance = hsl.Luminance * 0.4 }),
            FromHsl(hsl with { Luminance = hsl.Luminance * 0.2 })
        ];
    }

    public static IReadOnlyList<BaseColour> RotateHue(BaseColour colour, params double[] offsets)
    {
        var hsl = ToHsl(colour);
        return offsets.Select(offset => FromHsl(hsl with { Hue = (hsl.Hue + offset + 360d) % 360d })).ToArray();
    }

    public static BaseColour FromHsl(Hsl hsl)
    {
        var chroma = (1d - Math.Abs(2d * hsl.Luminance - 1d)) * hsl.Saturation;
        var x = chroma * (1d - Math.Abs((hsl.Hue / 60d) % 2d - 1d));
        var m = hsl.Luminance - chroma / 2d;
        var (red, green, blue) = hsl.Hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };

        return new(ToByte(red + m), ToByte(green + m), ToByte(blue + m));
    }

    public static Hsv ToHsv(BaseColour colour)
    {
        var red = colour.Red / 255d;
        var green = colour.Green / 255d;
        var blue = colour.Blue / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var saturation = maximum == 0 ? 0 : delta / maximum;

        if (delta == 0)
            return new(0, 0, maximum);

        var hue = maximum == red
            ? 60d * (((green - blue) / delta) % 6d)
            : maximum == green
                ? 60d * (((blue - red) / delta) + 2d)
                : 60d * (((red - green) / delta) + 4d);
        if (hue < 0)
            hue += 360d;
        return new(hue, saturation, maximum);
    }

    public static BaseColour FromHsv(Hsv hsv)
    {
        var chroma = hsv.Value * hsv.Saturation;
        var x = chroma
            * (1d - Math.Abs((hsv.Hue / 60d) % 2d - 1d));
        var m = hsv.Value - chroma;
        var (red, green, blue) = hsv.Hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };

        return new(ToByte(red + m), ToByte(green + m), ToByte(blue + m));
    }

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255d);
}
