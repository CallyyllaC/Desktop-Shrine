using DesktopShrine.Contracts.Audio;
using DesktopShrine.Contracts.Hardware;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Plugin.BlinkStickBar;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class BlinkStickBarTests
{
    [Fact]
    public void AudioSpectrumUsesWhiteChannelWithoutArtwork()
    {
        var settings = TestSettings(24) with
        {
            Attack = 1,
            PeakRise = 1,
            Gamma = 1
        };
        var renderer = new AudioSpectrumRenderer(settings);

        renderer.Update(AudioFrame(0.2f));
        var output = renderer.Render();

        Assert.Equal(settings.LedCount * 4, output.Length);
        Assert.True(WhiteTotal(output) > 0);
        Assert.Equal(0, RgbTotal(output));
    }

    [Fact]
    public void ArtworkPaletteColoursTheAudioSpectrum()
    {
        var settings = TestSettings(24) with
        {
            Attack = 1,
            PeakRise = 1,
            Gamma = 1
        };
        var renderer = new AudioSpectrumRenderer(settings);
        renderer.Update(Palette(
            new(20, 40, 80),
            new(30, 100, 180),
            new(180, 50, 30),
            new(240, 180, 80)));

        renderer.Update(AudioFrame(0.2f));
        var output = renderer.Render();

        Assert.True(RgbTotal(output) > 0);
        Assert.Equal(0, WhiteTotal(output));
    }

    [Fact]
    public void ArtworkColoursFollowPerceivedBrightnessAndSignalLevel()
    {
        var palette = AudioSpectrumRenderer.CreatePerceivedPalette(
            dark: new(255, 255, 255),
            dominant: new(128, 128, 128),
            accent: new(0, 0, 0),
            light: new(64, 64, 64));

        Assert.Equal(new RgbColour(0, 0, 0), palette[0]);
        Assert.Equal(new RgbColour(64, 64, 64), palette[1]);
        Assert.Equal(new RgbColour(128, 128, 128), palette[2]);
        Assert.Equal(new RgbColour(255, 255, 255), palette[3]);
        Assert.Equal(
            palette[0],
            AudioSpectrumRenderer.ColourForLevel(palette, 0));
        Assert.Equal(
            palette[^1],
            AudioSpectrumRenderer.ColourForLevel(palette, 1));

        var middle = AudioSpectrumRenderer.ColourForLevel(palette, 0.5f);
        Assert.InRange(middle.Red, (byte)65, (byte)127);
        Assert.Equal(middle.Red, middle.Green);
        Assert.Equal(middle.Red, middle.Blue);
    }

    [Fact]
    public void HardwareRendererProducesOneRgbwTuplePerLed()
    {
        var settings = TestSettings(24);
        var renderer = new HardwareWaveRenderer(settings);
        var now = DateTimeOffset.UtcNow;
        renderer.Update(State(now, 70, 45, 180, 65, 55, 70));

        var output = RenderSettled(renderer, now);

        Assert.Equal(24 * 4, output.Length);
        Assert.Contains(output, value => value > 0);
    }

    [Fact]
    public void HardwareGammaKeepsBackgroundDimRelativeToWaveCrests()
    {
        var contrastedSettings = TestSettings(24) with
        {
            HardwareGamma = 2.2f,
            LoadSmoothingSeconds = 0.01f,
            PowerSmoothingSeconds = 0.01f,
            TemperatureSmoothingSeconds = 0.01f,
            WhiteCrestThreshold = 1,
            WhiteCrestStrength = 0
        };
        var linearSettings = contrastedSettings with { HardwareGamma = 1 };
        var now = DateTimeOffset.UtcNow;
        var contrasted = new HardwareWaveRenderer(contrastedSettings);
        var linear = new HardwareWaveRenderer(linearSettings);
        var state = State(now, 25, 45, 38, 40, 45, 45);
        contrasted.Update(state);
        linear.Update(state);

        var contrastedFrame = RenderSettled(contrasted, now);
        var linearFrame = RenderSettled(linear, now);

        Assert.True(
            CrestContrast(contrastedFrame)
                > CrestContrast(linearFrame) * 1.8);
        Assert.Contains(contrastedFrame, value => value > 0);
    }

    [Fact]
    public void WavesStartAtTheSeamAndTravelTowardOppositeEdges()
    {
        var settings = TestSettings(24) with
        {
            IdleBrightness = 0,
            IdleWaveSpeedPixelsPerSecond = 4,
            MaximumWaveSpeedPixelsPerSecond = 4,
            IdleWaveSpacingPixels = 24,
            MinimumWaveSpacingPixels = 24,
            IdleCrestWidthPixels = 0.3f,
            ActiveCrestWidthPixels = 0.3f,
            MinimumTrailPixels = 0,
            MaximumTrailFraction = 0,
            LoadSmoothingSeconds = 0.01f,
            WhiteCrestThreshold = 1,
            WhiteCrestStrength = 0
        };
        var renderer = new HardwareWaveRenderer(settings);
        var now = DateTimeOffset.UtcNow;
        renderer.Update(State(now, 100, 100, 0, 0, 40, 40));

        var output = renderer.Render(TimeSpan.FromSeconds(0.25), now);
        var gpuCrest = BrightestPixel(output, 0, 12);
        var cpuCrest = BrightestPixel(output, 12, 12);

        Assert.Equal(10, gpuCrest);
        Assert.Equal(13, cpuCrest);
        Assert.Equal(11 - gpuCrest, cpuCrest - 12);
    }

    [Fact]
    public void TemperatureMapsGpuAndCpuHalvesIndependently()
    {
        var settings = TestSettings(24) with
        {
            LoadSmoothingSeconds = 0.01f,
            TemperatureSmoothingSeconds = 0.01f,
            WhiteCrestThreshold = 1,
            WhiteCrestStrength = 0
        };
        var renderer = new HardwareWaveRenderer(settings);
        var now = DateTimeOffset.UtcNow;
        renderer.Update(State(now, 85, 85, 120, 70, 92, 30));

        var output = RenderSettled(renderer, now);

        Assert.True(ChannelTotal(output, 0, 12, redOffset: 1)
            > ChannelTotal(output, 0, 12, redOffset: 2));
        Assert.True(ChannelTotal(output, 12, 12, redOffset: 2)
            > ChannelTotal(output, 12, 12, redOffset: 1));
    }

    [Fact]
    public void HighIntensityCrestsUseMoreWhiteThanLowLoadWaves()
    {
        var settings = TestSettings(24) with
        {
            LoadSmoothingSeconds = 0.01f,
            WhiteCrestThreshold = 0.6f,
            WhiteCrestStrength = 1
        };
        var now = DateTimeOffset.UtcNow;
        var low = new HardwareWaveRenderer(settings);
        var high = new HardwareWaveRenderer(settings);
        low.Update(State(now, 20, 20, 60, 30, 45, 45));
        high.Update(State(now, 100, 100, 250, 120, 45, 45));

        var lowFrame = RenderSettled(low, now);
        var highFrame = RenderSettled(high, now);

        Assert.True(WhiteTotal(highFrame) > WhiteTotal(lowFrame));
        Assert.Equal(0, WhiteTotal(lowFrame));
    }

    [Fact]
    public void HigherPowerDrawExtendsTheWaveTrail()
    {
        var settings = TestSettings(24) with
        {
            IdleBrightness = 0,
            IdleWaveSpeedPixelsPerSecond = 2,
            MaximumWaveSpeedPixelsPerSecond = 2,
            IdleWaveSpacingPixels = 24,
            MinimumWaveSpacingPixels = 24,
            IdleCrestWidthPixels = 0.35f,
            ActiveCrestWidthPixels = 0.35f,
            MinimumTrailPixels = 0,
            MaximumTrailFraction = 0.8f,
            LoadSmoothingSeconds = 0.01f,
            PowerSmoothingSeconds = 0.01f,
            WhiteCrestThreshold = 1,
            WhiteCrestStrength = 0
        };
        var now = DateTimeOffset.UtcNow;
        var lowPower = new HardwareWaveRenderer(settings);
        var highPower = new HardwareWaveRenderer(settings);
        lowPower.Update(State(now, 80, 80, 0, 0, 45, 45));
        highPower.Update(State(now, 80, 80, 300, 150, 45, 45));

        var lowFrame = RenderSettled(lowPower, now);
        var highFrame = RenderSettled(highPower, now);

        Assert.True(LitPixelCount(highFrame) > LitPixelCount(lowFrame));
    }

    [Fact]
    public void SuddenLoadIncreaseCreatesAnImmediatePulse()
    {
        var settings = TestSettings(24) with
        {
            IdleBrightness = 0,
            LoadSmoothingSeconds = 10,
            ImpulseThreshold = 0.05f,
            ImpulseGain = 1,
            ImpulseDecaySeconds = 1
        };
        var now = DateTimeOffset.UtcNow;
        var renderer = new HardwareWaveRenderer(settings);
        renderer.Update(State(now, 10, 10, 20, 10, 45, 45));
        var before = RenderSettled(renderer, now);

        renderer.Update(State(now, 95, 95, 250, 120, 45, 45));
        var after = renderer.Render(TimeSpan.FromSeconds(0.02), now);

        Assert.True(FrameEnergy(after) > FrameEnergy(before) * 1.5);
    }

    [Fact]
    public void MissingOrStaleTelemetryFallsBackToDimIdlePulses()
    {
        var settings = TestSettings(24);
        var renderer = new HardwareWaveRenderer(settings);
        var capturedAt = DateTimeOffset.UtcNow;
        renderer.Update(new()
        {
            IsAvailable = true,
            CapturedAt = capturedAt,
            Provider = "test",
            System = new(),
            GraphicsProcessors = []
        });

        var output = renderer.Render(
            TimeSpan.FromSeconds(0.1),
            capturedAt + settings.TelemetryStaleAfter + TimeSpan.FromSeconds(1));

        Assert.Equal(settings.LedCount * 4, output.Length);
        Assert.Contains(output, value => value > 0);
        Assert.True(FrameEnergy(output) < settings.LedCount * 4 * 32);
    }

    [Fact]
    public void RgbwPixelCountMustSplitIntoEqualHalves()
    {
        var odd = TestSettings(23);
        var tooLarge = TestSettings(
            BlinkStickBarSettings.MaximumRgbwPixelCount + 2);

        Assert.False(BlinkStickBarSettings.Validate(odd).IsValid);
        Assert.False(BlinkStickBarSettings.Validate(tooLarge).IsValid);
    }

    [Fact]
    public void FortyPixelRgbwFrameUsesTheLargestBlinkStickFeatureReport()
    {
        var frame = Enumerable.Range(0, 40 * 4)
            .Select(value => (byte)value)
            .ToArray();

        var report = BlinkStickProtocol.CreateIndexedFrameReport(
            channel: 2,
            frame);

        Assert.Equal(194, report.Length);
        Assert.Equal(9, report[0]);
        Assert.Equal(2, report[1]);
        Assert.Equal(frame, report[2..(frame.Length + 2)]);
        Assert.All(
            report[(frame.Length + 2)..],
            value => Assert.Equal(0, value));
    }

    private static BlinkStickBarSettings TestSettings(int ledCount)
    {
        var configuration = new ConfigurationBuilder().Build();
        return BlinkStickBarSettings.FromConfiguration(configuration) with
        {
            LedCount = ledCount,
            UsbCurrentMa = 10_000,
            ExternalPower = true,
            Brightness = 1
        };
    }

    private static HardwareMonitorState State(
        DateTimeOffset capturedAt,
        double gpuLoad,
        double cpuLoad,
        double gpuPower,
        double cpuPower,
        double gpuTemperature,
        double cpuTemperature) => new()
    {
        IsAvailable = true,
        CapturedAt = capturedAt,
        Provider = "test",
        System = new()
        {
            CpuUsagePercent = cpuLoad,
            CpuPowerWatts = cpuPower,
            CpuTemperatureCelsius = cpuTemperature
        },
        GraphicsProcessors =
        [
            new()
            {
                Id = 0,
                Name = "GPU",
                Type = GraphicsProcessorType.Discrete,
                UsagePercent = gpuLoad,
                TotalBoardPowerWatts = gpuPower,
                HotspotTemperatureCelsius = gpuTemperature
            }
        ]
    };

    private static AudioSpectrumFrame AudioFrame(float magnitude) => new()
    {
        CapturedAt = DateTimeOffset.UtcNow,
        Sequence = 1,
        SampleRate = 48000,
        FftSize = 2048,
        Spectrum = Enumerable.Repeat(magnitude, 1025).ToArray(),
        Waveform = Enumerable.Repeat(magnitude, 160).ToArray()
    };

    private static MediaColourPalette Palette(
        BaseColour dark,
        BaseColour dominant,
        BaseColour accent,
        BaseColour light) => new(
            new PaletteColour(dominant),
            new PaletteColour(accent),
            new PaletteColour(dark),
            new PaletteColour(light),
            new PaletteStatistics());

    private static byte[] RenderSettled(
        HardwareWaveRenderer renderer,
        DateTimeOffset now)
    {
        byte[] output = [];
        for (var index = 0; index < 8; index++)
        {
            output = renderer.Render(
                TimeSpan.FromSeconds(0.25),
                now + TimeSpan.FromSeconds(index * 0.25));
        }
        return output;
    }

    private static int BrightestPixel(
        byte[] frame,
        int start,
        int count) =>
        Enumerable.Range(start, count)
            .OrderByDescending(index => PixelEnergy(frame, index))
            .First();

    private static int ChannelTotal(
        byte[] frame,
        int start,
        int count,
        int redOffset) =>
        Enumerable.Range(start, count)
            .Sum(index => frame[(index * 4) + redOffset]);

    private static int WhiteTotal(byte[] frame) =>
        Enumerable.Range(0, frame.Length / 4)
            .Sum(index => frame[(index * 4) + 3]);

    private static int RgbTotal(byte[] frame) =>
        Enumerable.Range(0, frame.Length / 4)
            .Sum(index => frame[index * 4]
                + frame[(index * 4) + 1]
                + frame[(index * 4) + 2]);

    private static int LitPixelCount(byte[] frame) =>
        Enumerable.Range(0, frame.Length / 4)
            .Count(index => PixelEnergy(frame, index) > 6);

    private static int FrameEnergy(byte[] frame) =>
        Enumerable.Range(0, frame.Length / 4)
            .Sum(index => PixelEnergy(frame, index));

    private static double CrestContrast(byte[] frame)
    {
        var energies = Enumerable.Range(0, frame.Length / 4)
            .Select(index => PixelEnergy(frame, index))
            .Order()
            .ToArray();
        return energies[^1] / (energies[energies.Length / 2] + 1d);
    }

    private static int PixelEnergy(byte[] frame, int index)
    {
        var offset = index * 4;
        return frame[offset]
            + frame[offset + 1]
            + frame[offset + 2]
            + frame[offset + 3];
    }
}
