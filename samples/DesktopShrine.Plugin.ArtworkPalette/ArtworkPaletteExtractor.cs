using DesktopShrine.Contracts.Media;

namespace DesktopShrine.Plugin.ArtworkPalette;

internal static class ArtworkPaletteExtractor
{
    internal const byte AlphaThreshold = 24;
    internal const double MaximumAccentLightness = 0.72;

    public static MediaColourPalette Extract(
        ReadOnlySpan<byte> rgbaPixels,
        int width,
        int height,
        bool isPremultiplied = false,
        ArtworkPaletteSettings? settings = null)
    {
        settings ??= new();
        if (width <= 0 || height <= 0 || rgbaPixels.Length != checked(width * height * 4))
            throw new ArgumentException("RGBA pixel data does not match the supplied dimensions.", nameof(rgbaPixels));

        if (!TryFindOpaqueBounds(
                rgbaPixels,
                width,
                height,
                settings.AlphaThreshold,
                out var bounds))
            return MediaColourPalette.Unavailable;

        byte[]? resizedPixels = null;
        if (bounds.Width > settings.MaximumAnalysisDimension
            || bounds.Height > settings.MaximumAnalysisDimension)
        {
            resizedPixels = ResizeForAnalysis(
                rgbaPixels,
                width,
                bounds,
                isPremultiplied,
                settings);
            rgbaPixels = resizedPixels;
            width = AnalysisSize(
                bounds.Width,
                bounds.Height,
                settings.MaximumAnalysisDimension).Width;
            height = AnalysisSize(
                bounds.Width,
                bounds.Height,
                settings.MaximumAnalysisDimension).Height;
            bounds = new(0, 0, width, height);
            isPremultiplied = false;
        }

        var buckets = new Dictionary<int, ColourBucket>();
        var borderWidth = settings.BorderPercent == 0
            ? 0
            : Math.Max(
                1,
                (int)Math.Round(bounds.Width * settings.BorderPercent));
        var borderHeight = settings.BorderPercent == 0
            ? 0
            : Math.Max(
                1,
                (int)Math.Round(bounds.Height * settings.BorderPercent));

        for (var y = 0; y < bounds.Height; y++)
        {
            for (var x = 0; x < bounds.Width; x++)
            {
                var offset = ((bounds.Y + y) * width + bounds.X + x) * 4;
                var alpha = rgbaPixels[offset + 3];
                if (alpha < settings.AlphaThreshold)
                    continue;

                var red = StraightChannel(rgbaPixels[offset], alpha, isPremultiplied);
                var green = StraightChannel(rgbaPixels[offset + 1], alpha, isPremultiplied);
                var blue = StraightChannel(rgbaPixels[offset + 2], alpha, isPremultiplied);
                var key = ((red >> 4) << 8) | ((green >> 4) << 4) | (blue >> 4);
                var isBorder =
                    x < borderWidth ||
                    x >= bounds.Width - borderWidth ||
                    y < borderHeight ||
                    y >= bounds.Height - borderHeight;
                var weight = alpha / 255d
                    * (isBorder ? settings.BorderWeight : 1d);

                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new();
                    buckets.Add(key, bucket);
                }
                bucket.Add(red, green, blue, weight, alpha / 255d);
            }
        }

        if (buckets.Count == 0)
            return MediaColourPalette.Unavailable;

        var colours = buckets.Values.Select(bucket => bucket.Finish()).ToArray();
        var dominant = colours.MaxBy(colour => colour.Weight)!;
        var accent = SelectAccent(colours, dominant, settings);
        var dark = SelectDark(colours, dominant);
        var light = SelectLight(colours, dominant);
        var statistics = PaletteStatistics.FromSamples(
            colours.Select(colour => new WeightedPaletteSample(colour.Colour, colour.Weight)));

        return new(
            new(dominant.Colour),
            new(accent),
            new(dark),
            new(light),
            statistics);
    }

    internal static byte UnPremultiplyChannel(byte channel, byte alpha)
    {
        if (alpha == 0)
            return 0;

        return (byte)Math.Clamp(
            (int)Math.Round(channel * 255d / alpha),
            byte.MinValue,
            byte.MaxValue);
    }

    private static byte StraightChannel(byte channel, byte alpha, bool isPremultiplied) =>
        isPremultiplied ? UnPremultiplyChannel(channel, alpha) : channel;

    private static bool TryFindOpaqueBounds(
        ReadOnlySpan<byte> rgbaPixels,
        int width,
        int height,
        byte alphaThreshold,
        out PixelBounds bounds)
    {
        var minimumX = width;
        var minimumY = height;
        var maximumX = -1;
        var maximumY = -1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (rgbaPixels[(y * width + x) * 4 + 3] < alphaThreshold)
                    continue;

                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
            }
        }

        if (maximumX < minimumX || maximumY < minimumY)
        {
            bounds = default;
            return false;
        }

        bounds = new(
            minimumX,
            minimumY,
            maximumX - minimumX + 1,
            maximumY - minimumY + 1);
        return true;
    }

    private static byte[] ResizeForAnalysis(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        PixelBounds bounds,
        bool isPremultiplied,
        ArtworkPaletteSettings settings)
    {
        var targetSize = AnalysisSize(
            bounds.Width,
            bounds.Height,
            settings.MaximumAnalysisDimension);
        var target = new byte[targetSize.Width * targetSize.Height * 4];
        var scaleX = bounds.Width / (double)targetSize.Width;
        var scaleY = bounds.Height / (double)targetSize.Height;

        // Resample in premultiplied space so hidden RGB in transparent pixels cannot
        // bleed into edge colours, then store straight RGBA for palette analysis.
        for (var targetY = 0; targetY < targetSize.Height; targetY++)
        {
            var sourceTop = targetY * scaleY;
            var sourceBottom = (targetY + 1) * scaleY;
            var firstY = (int)Math.Floor(sourceTop);
            var finalY = Math.Min(bounds.Height - 1, (int)Math.Ceiling(sourceBottom) - 1);

            for (var targetX = 0; targetX < targetSize.Width; targetX++)
            {
                var sourceLeft = targetX * scaleX;
                var sourceRight = (targetX + 1) * scaleX;
                var firstX = (int)Math.Floor(sourceLeft);
                var finalX = Math.Min(bounds.Width - 1, (int)Math.Ceiling(sourceRight) - 1);
                var alphaSum = 0d;
                var redSum = 0d;
                var greenSum = 0d;
                var blueSum = 0d;
                var coverageSum = 0d;

                for (var sourceY = firstY; sourceY <= finalY; sourceY++)
                {
                    var coverageY = Math.Min(sourceBottom, sourceY + 1d) - Math.Max(sourceTop, sourceY);
                    for (var sourceX = firstX; sourceX <= finalX; sourceX++)
                    {
                        var coverageX = Math.Min(sourceRight, sourceX + 1d) - Math.Max(sourceLeft, sourceX);
                        var coverage = coverageX * coverageY;
                        var offset = ((bounds.Y + sourceY) * sourceWidth + bounds.X + sourceX) * 4;
                        coverageSum += coverage;
                        var alphaByte = source[offset + 3];
                        if (alphaByte < settings.AlphaThreshold)
                            continue;

                        var alpha = alphaByte / 255d;
                        var red = StraightChannel(source[offset], alphaByte, isPremultiplied);
                        var green = StraightChannel(source[offset + 1], alphaByte, isPremultiplied);
                        var blue = StraightChannel(source[offset + 2], alphaByte, isPremultiplied);

                        alphaSum += alpha * coverage;
                        redSum += red * alpha * coverage;
                        greenSum += green * alpha * coverage;
                        blueSum += blue * alpha * coverage;
                    }
                }

                var targetOffset = (targetY * targetSize.Width + targetX) * 4;
                target[targetOffset + 3] = ToByte(alphaSum / coverageSum);
                if (alphaSum <= 0)
                    continue;

                target[targetOffset] = ToByte(redSum / alphaSum / 255d);
                target[targetOffset + 1] = ToByte(greenSum / alphaSum / 255d);
                target[targetOffset + 2] = ToByte(blueSum / alphaSum / 255d);
            }
        }

        return target;
    }

    private static (int Width, int Height) AnalysisSize(
        int width,
        int height,
        int maximumDimension)
    {
        var scale = Math.Min(
            1d,
            maximumDimension / (double)Math.Max(width, height));
        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static BaseColour SelectAccent(
        IReadOnlyCollection<AnalysedColour> colours,
        AnalysedColour dominant,
        ArtworkPaletteSettings settings)
    {
        var distinctCandidates = colours
            .Where(colour =>
                colour.Colour != dominant.Colour &&
                PerceptualDistance(colour.Colour, dominant.Colour)
                    >= settings.MinimumDistinctAccentDistance)
            .ToArray();
        if (distinctCandidates.Length == 0)
            return RotateHue(dominant.Colour, 180);

        var alphaQualifiedCandidates = distinctCandidates
            .Where(colour =>
                colour.AverageAlpha >= settings.MinimumAccentAverageAlpha)
            .ToArray();
        if (alphaQualifiedCandidates.Length > 0)
            distinctCandidates = alphaQualifiedCandidates;

        var eligibleCandidates = distinctCandidates
            .Where(colour =>
                colour.HslLightness <= settings.MaximumAccentLightness)
            .ToArray();
        var candidates = eligibleCandidates.Length > 0 ? eligibleCandidates : distinctCandidates;
        var maximumWeight = candidates.Max(colour => colour.Weight);

        return candidates.MaxBy(colour =>
        {
            var populationWeight = Math.Sqrt(colour.Weight / maximumWeight);
            var chromaWeight = Math.Pow(Math.Max(colour.Saturation, 0.01), 1.35);
            var distanceWeight = PerceptualDistance(colour.Colour, dominant.Colour);
            var palePenalty =
                colour.HslLightness > settings.MaximumAccentLightness
                    ? 0.05
                    : 1d;
            return populationWeight * chromaWeight * distanceWeight * palePenalty;
        })!.Colour;
    }

    private static BaseColour SelectDark(IReadOnlyCollection<AnalysedColour> colours, AnalysedColour dominant)
    {
        var candidates = colours.Where(colour => colour.RelativeLuminance <= 0.32).ToArray();
        if (candidates.Length == 0)
            return Scale(dominant.Colour, 0.28);

        return candidates.MaxBy(colour =>
            Math.Pow(colour.Weight, 0.4) *
            Math.Pow(1.05 - colour.RelativeLuminance, 2) *
            (0.35 + PerceptualDistance(colour.Colour, dominant.Colour)))!.Colour;
    }

    private static BaseColour SelectLight(IReadOnlyCollection<AnalysedColour> colours, AnalysedColour dominant)
    {
        var candidates = colours.Where(colour => colour.RelativeLuminance >= 0.68).ToArray();
        if (candidates.Length == 0)
            return Mix(dominant.Colour, new(255, 255, 255), 0.65);

        return candidates.MaxBy(colour =>
            Math.Pow(colour.Weight, 0.4) *
            Math.Pow(colour.RelativeLuminance + 0.05, 2) *
            (0.35 + PerceptualDistance(colour.Colour, dominant.Colour)))!.Colour;
    }

    private static double PerceptualDistance(BaseColour first, BaseColour second)
    {
        var a = ToOklab(first);
        var b = ToOklab(second);
        var deltaL = a.Lightness - b.Lightness;
        var deltaA = a.A - b.A;
        var deltaB = a.B - b.B;
        return Math.Sqrt(deltaL * deltaL + deltaA * deltaA + deltaB * deltaB);
    }

    private static Oklab ToOklab(BaseColour colour)
    {
        var red = ToLinear(colour.Red / 255d);
        var green = ToLinear(colour.Green / 255d);
        var blue = ToLinear(colour.Blue / 255d);
        var l = Math.Cbrt(0.4122214708 * red + 0.5363325363 * green + 0.0514459929 * blue);
        var m = Math.Cbrt(0.2119034982 * red + 0.6806995451 * green + 0.1073969566 * blue);
        var s = Math.Cbrt(0.0883024619 * red + 0.2817188376 * green + 0.6299787005 * blue);
        return new(
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static double ToLinear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static BaseColour RotateHue(BaseColour colour, double degrees)
    {
        var hsl = ToHsl(colour);
        return FromHsl((hsl.Hue + degrees + 360d) % 360d, hsl.Saturation, hsl.Lightness);
    }

    private static BaseColour Scale(BaseColour colour, double amount) => new(
        (byte)Math.Round(colour.Red * amount),
        (byte)Math.Round(colour.Green * amount),
        (byte)Math.Round(colour.Blue * amount));

    private static BaseColour Mix(BaseColour first, BaseColour second, double secondAmount) => new(
        (byte)Math.Round(first.Red + (second.Red - first.Red) * secondAmount),
        (byte)Math.Round(first.Green + (second.Green - first.Green) * secondAmount),
        (byte)Math.Round(first.Blue + (second.Blue - first.Blue) * secondAmount));

    private static (double Hue, double Saturation, double Lightness) ToHsl(BaseColour colour)
    {
        var red = colour.Red / 255d;
        var green = colour.Green / 255d;
        var blue = colour.Blue / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var lightness = (maximum + minimum) / 2d;
        if (delta == 0)
            return (0, 0, lightness);

        var saturation = delta / (1d - Math.Abs(2d * lightness - 1d));
        var hue = maximum == red
            ? 60d * (((green - blue) / delta) % 6d)
            : maximum == green
                ? 60d * (((blue - red) / delta) + 2d)
                : 60d * (((red - green) / delta) + 4d);
        return (hue < 0 ? hue + 360d : hue, saturation, lightness);
    }

    private static BaseColour FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1d - Math.Abs(2d * lightness - 1d)) * saturation;
        var x = chroma * (1d - Math.Abs((hue / 60d) % 2d - 1d));
        var m = lightness - chroma / 2d;
        var (red, green, blue) = hue switch
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

    private sealed class ColourBucket
    {
        private double red;
        private double green;
        private double blue;
        private double weight;
        private double alphaSum;
        private int sampleCount;

        public void Add(
            byte redValue,
            byte greenValue,
            byte blueValue,
            double sampleWeight,
            double alpha)
        {
            red += redValue * sampleWeight;
            green += greenValue * sampleWeight;
            blue += blueValue * sampleWeight;
            weight += sampleWeight;
            alphaSum += alpha;
            sampleCount++;
        }

        public AnalysedColour Finish()
        {
            var colour = new BaseColour(
                (byte)Math.Round(red / weight),
                (byte)Math.Round(green / weight),
                (byte)Math.Round(blue / weight));
            var hsl = ToHsl(colour);
            return new(
                colour,
                weight,
                hsl.Saturation,
                hsl.Lightness,
                RelativeLuminance(colour),
                alphaSum / sampleCount);
        }

        private static double RelativeLuminance(BaseColour colour) =>
            0.2126 * ToLinear(colour.Red / 255d) +
            0.7152 * ToLinear(colour.Green / 255d) +
            0.0722 * ToLinear(colour.Blue / 255d);
    }

    private sealed record AnalysedColour(
        BaseColour Colour,
        double Weight,
        double Saturation,
        double HslLightness,
        double RelativeLuminance,
        double AverageAlpha);

    private readonly record struct PixelBounds(int X, int Y, int Width, int Height);
    private readonly record struct Oklab(double Lightness, double A, double B);
}
