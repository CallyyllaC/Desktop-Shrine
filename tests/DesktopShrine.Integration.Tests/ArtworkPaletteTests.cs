using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Plugin.ArtworkPalette;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class ArtworkPaletteTests
{
    [Fact]
    public void ExtractorAvoidsBorderAndFindsSmallVividAccent()
    {
        const int width = 10;
        const int height = 10;
        var pixels = SolidImage(width, height, new(0, 0, 0));
        Fill(pixels, width, 1, 1, 8, 8, new(230, 25, 25));
        Fill(pixels, width, 4, 4, 2, 2, new(20, 50, 240));

        var palette = ArtworkPaletteExtractor.Extract(pixels, width, height);

        Assert.True(palette.Dominant.BaseColour.Red > 200);
        Assert.True(palette.Accent.BaseColour.Blue > 200);
        Assert.True(palette.Dark.BaseColour.Red < 30);
        Assert.True(palette.Light.BaseColour.Red > palette.Dominant.BaseColour.Red);
    }

    [Fact]
    public void TransparentPixelsDoNotInfluencePalette()
    {
        const int width = 64;
        const int height = 64;
        var pixels = SolidImage(width, height, new(0, 255, 0), 0);
        Fill(pixels, width, 28, 28, 8, 8, new(240, 20, 20));
        var referencePixels = SolidImage(8, 8, new(240, 20, 20));

        var palette = ArtworkPaletteExtractor.Extract(pixels, width, height);
        var reference = ArtworkPaletteExtractor.Extract(referencePixels, 8, 8);

        AssertPalettesEqual(reference, palette);
        Assert.DoesNotContain(
            new[]
            {
                palette.Dominant.BaseColour,
                palette.Accent.BaseColour,
                palette.Dark.BaseColour,
                palette.Light.BaseColour
            },
            colour => colour == new BaseColour(0, 255, 0));
    }

    [Fact]
    public void TransparentOuterBorderDoesNotChangePalette()
    {
        const int subjectWidth = 160;
        const int subjectHeight = 80;
        var subject = SolidImage(subjectWidth, subjectHeight, new(45, 70, 120));
        Fill(subject, subjectWidth, 30, 20, 50, 40, new(220, 55, 40));
        Fill(subject, subjectWidth, 110, 10, 30, 60, new(80, 210, 190), 160);

        const int paddedWidth = 320;
        const int paddedHeight = 240;
        var padded = SolidImage(paddedWidth, paddedHeight, new(20, 255, 30), 0);
        Blit(subject, subjectWidth, subjectHeight, padded, paddedWidth, 80, 80);

        var tightPalette = ArtworkPaletteExtractor.Extract(subject, subjectWidth, subjectHeight);
        var paddedPalette = ArtworkPaletteExtractor.Extract(padded, paddedWidth, paddedHeight);

        AssertPalettesEqual(tightPalette, paddedPalette, tolerance: 1);
    }

    [Fact]
    public void PartiallyTransparentPixelsHaveLessPopulationWeight()
    {
        const int width = 10;
        const int height = 10;
        var partial = SolidImage(width, height, new(220, 35, 30));
        Fill(partial, width, 0, 0, 6, height, new(30, 60, 230), 64);
        var opaque = partial.ToArray();
        SetAlpha(opaque, width, 0, 0, 6, height, 255);

        var partialPalette = ArtworkPaletteExtractor.Extract(partial, width, height);
        var opaquePalette = ArtworkPaletteExtractor.Extract(opaque, width, height);

        Assert.True(partialPalette.Dominant.BaseColour.Red > 180);
        Assert.True(opaquePalette.Dominant.BaseColour.Blue > 180);
    }

    [Fact]
    public void PremultipliedAndStraightAlphaProduceEquivalentPalettes()
    {
        const int width = 8;
        const int height = 8;
        var straight = SolidImage(width, height, new(200, 100, 50), 128);
        Fill(straight, width, 2, 2, 4, 4, new(40, 160, 220), 204);
        var premultiplied = Premultiply(straight);

        var straightPalette = ArtworkPaletteExtractor.Extract(straight, width, height);
        var premultipliedPalette = ArtworkPaletteExtractor.Extract(
            premultiplied,
            width,
            height,
            isPremultiplied: true);

        AssertPalettesEqual(straightPalette, premultipliedPalette, tolerance: 1);
        Assert.Equal(0, ArtworkPaletteExtractor.UnPremultiplyChannel(200, 0));
        Assert.Equal(255, ArtworkPaletteExtractor.UnPremultiplyChannel(250, 10));
    }

    [Fact]
    public void FullyTransparentImageReturnsUnavailablePalette()
    {
        var pixels = SolidImage(20, 20, new(20, 240, 80), 0);

        var palette = ArtworkPaletteExtractor.Extract(pixels, 20, 20);

        Assert.Same(MediaColourPalette.Unavailable, palette);
        Assert.False(palette.IsAvailable);
    }

    [Fact]
    public void OutputSafeColoursAreLazyCachedAndDoNotChangeRawColours()
    {
        var dominant = new PaletteColour(new(49, 63, 45));
        var accent = new PaletteColour(new(187, 184, 233));
        var dark = new PaletteColour(new(49, 63, 45));
        var light = new PaletteColour(new(181, 236, 247));
        var palette = new MediaColourPalette(
            dominant,
            accent,
            dark,
            light,
            PaletteStatistics.FromSamples([
                new(dominant.BaseColour, 4),
                new(accent.BaseColour, 1),
                new(light.BaseColour, 1)
            ]));

        var outputAccent = palette.OutputAccent;
        var outputLight = palette.OutputLight;
        var rawAccentHsv = ToHsv(accent.BaseColour);
        var rawLightHsv = ToHsv(light.BaseColour);
        var outputAccentHsv = ToHsv(outputAccent.BaseColour);
        var outputLightHsv = ToHsv(outputLight.BaseColour);

        Assert.Equal(new BaseColour(187, 184, 233), palette.Accent.BaseColour);
        Assert.Equal(new BaseColour(181, 236, 247), palette.Light.BaseColour);
        Assert.Same(outputAccent, palette.OutputAccent);
        Assert.Same(outputLight, palette.OutputLight);
        AssertHueClose(rawAccentHsv.Hue, outputAccentHsv.Hue, 1);
        AssertHueClose(rawLightHsv.Hue, outputLightHsv.Hue, 1);
        Assert.InRange(
            outputAccentHsv.Saturation,
            MediaColourPalette.OutputAccentMinimumSaturation - 0.01,
            1);
        Assert.True(outputAccentHsv.Value >= rawAccentHsv.Value - 0.01);
        Assert.InRange(
            outputLightHsv.Saturation,
            MediaColourPalette.OutputLightMinimumSaturation - 0.01,
            1);
        Assert.True(outputLightHsv.Value >= rawLightHsv.Value - 0.01);
    }

    [Fact]
    public void OutputPaletteBorrowsHueForDarkNeutralsButPreservesWhite()
    {
        var dominant = new PaletteColour(new(2, 2, 2));
        var accent = new PaletteColour(new(30, 60, 220));
        var dark = new PaletteColour(new(20, 20, 20));
        var white = new PaletteColour(new(245, 245, 245));
        var palette = new MediaColourPalette(
            dominant,
            accent,
            dark,
            white,
            PaletteStatistics.FromSamples([
                new(accent.BaseColour, 1)
            ]));
        var reference = ToHsv(accent.BaseColour);
        var outputDominant = ToHsv(
            palette.OutputDominant.BaseColour);
        var outputDark = ToHsv(palette.OutputDark.BaseColour);

        AssertHueClose(reference.Hue, outputDominant.Hue, 1);
        AssertHueClose(reference.Hue, outputDark.Hue, 3);
        Assert.InRange(
            outputDominant.Saturation,
            MediaColourPalette.NearBlackSaturation - 0.01,
            MediaColourPalette.NearBlackSaturation + 0.01);
        Assert.True(
            outputDominant.Value
                >= MediaColourPalette.OutputDominantMinimumValue - 0.01);
        Assert.True(
            outputDark.Saturation
                >= MediaColourPalette.OutputDarkMinimumSaturation - 0.01);
        Assert.True(
            outputDark.Value
                >= MediaColourPalette.OutputDarkMinimumValue - 0.01);
        Assert.Equal(white.BaseColour, palette.OutputLight.BaseColour);
    }

    [Fact]
    public void OutputPaletteTurnsPureBlackIntoVeryDarkNeutralGray()
    {
        var black = new PaletteColour(new(0, 0, 0));
        var accent = new PaletteColour(new(220, 40, 80));
        var palette = new MediaColourPalette(
            black,
            accent,
            black,
            new(new BaseColour(255, 255, 255)),
            PaletteStatistics.FromSamples([
                new(accent.BaseColour, 1)
            ]));
        var expected = (byte)Math.Round(
            MediaColourPalette.PureBlackGrayValue * 255);

        Assert.Equal(
            new BaseColour(expected, expected, expected),
            palette.OutputDominant.BaseColour);
        Assert.Equal(
            new BaseColour(expected, expected, expected),
            palette.OutputDark.BaseColour);
    }

    [Fact]
    public void OutputPalettePreservesExistingChromaticHue()
    {
        var dominant = new PaletteColour(new(100, 60, 50));
        var palette = new MediaColourPalette(
            dominant,
            new(new BaseColour(40, 80, 220)),
            new(new BaseColour(20, 20, 20)),
            new(new BaseColour(240, 240, 240)),
            PaletteStatistics.FromSamples([
                new(dominant.BaseColour, 1)
            ]));
        var before = ToHsv(dominant.BaseColour);
        var after = ToHsv(palette.OutputDominant.BaseColour);

        AssertHueClose(before.Hue, after.Hue, 1);
        Assert.True(after.Saturation >= before.Saturation - 0.01);
        Assert.True(after.Value >= before.Value - 0.01);
    }

    [Fact]
    public void AccentPrefersMeaningfulSaturatedColourOverPaleLightCluster()
    {
        const int width = 20;
        const int height = 10;
        var pixels = SolidImage(width, height, new(35, 45, 55));
        Fill(pixels, width, 12, 0, 6, height, new(242, 244, 246));
        Fill(pixels, width, 18, 0, 2, height, new(30, 90, 230));

        var palette = ArtworkPaletteExtractor.Extract(pixels, width, height);

        Assert.True(palette.Dominant.BaseColour.Blue < 80);
        Assert.True(palette.Accent.BaseColour.Blue > 180);
        Assert.True(palette.Accent.BaseColour.Red < 100);
        Assert.True(palette.Light.BaseColour.Red > 220);
    }

    [Fact]
    public void AverageHueUsesCircularMeanAcrossZeroDegrees()
    {
        var statistics = PaletteStatistics.FromSamples([
            new(new(255, 0, 4), 1),
            new(new(255, 4, 0), 1)
        ]);
        var colour = new PaletteColour(new(255, 0, 0));
        var palette = new MediaColourPalette(colour, colour, colour, colour, statistics);

        Assert.True(palette.AverageHue < 2 || palette.AverageHue > 358);
        Assert.Equal(ColourTemperature.Warm, palette.ColourTemperature);
    }

    [Fact]
    public void HarmonyCollectionsAreCachedAndHaveExpectedSizes()
    {
        var colour = new PaletteColour(new(220, 60, 40));

        Assert.Same(colour.Shades, colour.Shades);
        Assert.Equal(4, colour.Shades.Count);
        Assert.Equal(2, colour.Analogous.Count);
        Assert.Equal(2, colour.SplitComplementary.Count);
        Assert.Equal(2, colour.Triadic.Count);
        Assert.Equal(3, colour.Tetradic.Count);
    }

    [Fact]
    public void UnavailablePaletteExplicitlyRepresentsMissingArtwork()
    {
        Assert.False(MediaColourPalette.Unavailable.IsAvailable);
    }

    [Fact]
    public async Task MissingArtworkPublishesUnavailableStateOnlyOnce()
    {
        var context = new CapturingPluginContext();
        await using var plugin = new ArtworkPalettePlugin();
        await plugin.InitialiseAsync(context, TestContext.Current.CancellationToken);
        var missingArtwork = new NowPlayingState
        {
            IsAvailable = true,
            CapturedAt = DateTimeOffset.UtcNow,
            Artwork = null
        };

        await context.Subscriber.DeliverAsync(
            missingArtwork,
            TestContext.Current.CancellationToken);
        await context.Subscriber.DeliverAsync(
            missingArtwork,
            TestContext.Current.CancellationToken);

        var published = Assert.Single(context.Publisher.Messages);
        var palette = Assert.IsType<MediaColourPalette>(published);
        Assert.False(palette.IsAvailable);
    }

    private static byte[] SolidImage(int width, int height, BaseColour colour, byte alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        Fill(pixels, width, 0, 0, width, height, colour, alpha);
        return pixels;
    }

    private static void Fill(byte[] pixels, int imageWidth, int x, int y, int width, int height, BaseColour colour, byte alpha = 255)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                var offset = (row * imageWidth + column) * 4;
                pixels[offset] = colour.Red;
                pixels[offset + 1] = colour.Green;
                pixels[offset + 2] = colour.Blue;
                pixels[offset + 3] = alpha;
            }
        }
    }

    private static void SetAlpha(
        byte[] pixels,
        int imageWidth,
        int x,
        int y,
        int width,
        int height,
        byte alpha)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
                pixels[(row * imageWidth + column) * 4 + 3] = alpha;
        }
    }

    private static void Blit(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        byte[] target,
        int targetWidth,
        int targetX,
        int targetY)
    {
        for (var row = 0; row < sourceHeight; row++)
        {
            source.AsSpan(row * sourceWidth * 4, sourceWidth * 4).CopyTo(
                target.AsSpan(((targetY + row) * targetWidth + targetX) * 4));
        }
    }

    private static byte[] Premultiply(byte[] straightPixels)
    {
        var result = straightPixels.ToArray();
        for (var offset = 0; offset < result.Length; offset += 4)
        {
            var alpha = result[offset + 3] / 255d;
            result[offset] = (byte)Math.Round(result[offset] * alpha);
            result[offset + 1] = (byte)Math.Round(result[offset + 1] * alpha);
            result[offset + 2] = (byte)Math.Round(result[offset + 2] * alpha);
        }
        return result;
    }

    private static void AssertPalettesEqual(
        MediaColourPalette expected,
        MediaColourPalette actual,
        int tolerance = 0)
    {
        AssertColourClose(expected.Dominant.BaseColour, actual.Dominant.BaseColour, tolerance);
        AssertColourClose(expected.Accent.BaseColour, actual.Accent.BaseColour, tolerance);
        AssertColourClose(expected.Dark.BaseColour, actual.Dark.BaseColour, tolerance);
        AssertColourClose(expected.Light.BaseColour, actual.Light.BaseColour, tolerance);
        Assert.InRange(Math.Abs(expected.AverageHue - actual.AverageHue), 0, tolerance);
        Assert.InRange(Math.Abs(expected.AverageSaturation - actual.AverageSaturation), 0, tolerance / 255d);
        Assert.InRange(Math.Abs(expected.AverageLuminance - actual.AverageLuminance), 0, tolerance / 255d);
    }

    private static void AssertColourClose(BaseColour expected, BaseColour actual, int tolerance)
    {
        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, tolerance);
    }

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

    private static (double Hue, double Saturation, double Value) ToHsv(
        BaseColour colour)
    {
        var red = colour.Red / 255d;
        var green = colour.Green / 255d;
        var blue = colour.Blue / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        if (delta == 0)
            return (0, 0, maximum);

        var saturation = maximum == 0 ? 0 : delta / maximum;
        var hue = maximum == red
            ? 60d * (((green - blue) / delta) % 6d)
            : maximum == green
                ? 60d * (((blue - red) / delta) + 2d)
                : 60d * (((red - green) / delta) + 4d);
        return (
            hue < 0 ? hue + 360d : hue,
            saturation,
            maximum);
    }

    private static void AssertHueClose(double expected, double actual, double tolerance)
    {
        var difference = Math.Abs(expected - actual);
        Assert.True(Math.Min(difference, 360 - difference) <= tolerance);
    }

    private sealed class CapturingPluginContext : IPluginContext
    {
        public CapturingPluginContext()
        {
            Publisher = new();
            Subscriber = new();
        }

        public string PluginId => "artwork-palette";
        public IConfiguration Configuration { get; } =
            new ConfigurationBuilder().Build();
        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
        IPluginPublisher IPluginContext.Publisher => Publisher;
        IPluginSubscriber IPluginContext.Subscriber => Subscriber;
        public CapturingPublisher Publisher { get; }
        public CapturingSubscriber Subscriber { get; }
        public ILiveConfiguration<OutputInputProfile>? InputProfile => null;

        public ILiveConfiguration<TConfig> ObserveConfiguration<TConfig>(
            Func<IConfiguration, TConfig> snapshotFactory,
            Func<TConfig, ConfigurationValidationResult>? validator = null)
            where TConfig : notnull =>
            new StaticLiveConfiguration<TConfig>(snapshotFactory(Configuration));
    }

    private sealed class StaticLiveConfiguration<TConfig>(TConfig current) :
        ILiveConfiguration<TConfig>
        where TConfig : notnull
    {
        public TConfig Current => current;
        public event EventHandler<ConfigurationChangedEventArgs<TConfig>>? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class CapturingPublisher : IPluginPublisher
    {
        public List<IShrineMessage> Messages { get; } = [];

        public ValueTask PublishAsync<T>(
            string providedPortId,
            T payload,
            CancellationToken cancellationToken = default)
            where T : IShrineMessage
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(payload);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingSubscriber : IPluginSubscriber
    {
        private Func<MessageEnvelope<NowPlayingState>, CancellationToken, ValueTask>? handler;

        public IAsyncDisposable Subscribe<T>(
            string requiredPortId,
            Func<MessageEnvelope<T>, CancellationToken, ValueTask> value)
            where T : IShrineMessage
        {
            handler = (Func<MessageEnvelope<NowPlayingState>, CancellationToken, ValueTask>)(object)value;
            return new EmptySubscription();
        }

        public IInputRouteMonitor ObserveRoute(string requiredPortId) =>
            throw new NotSupportedException();

        public ValueTask DeliverAsync(NowPlayingState state, CancellationToken token) =>
            handler!(
                new()
                {
                    MessageId = Guid.NewGuid(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Source = new("windows-now-playing", "now-playing"),
                    Contract = new("desktop-shrine.media.now-playing", new(1, 2, 0)),
                    Payload = state
                },
                token);
    }

    private sealed class EmptySubscription : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
