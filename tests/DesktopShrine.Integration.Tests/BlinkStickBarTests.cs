using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Audio;
using DesktopShrine.Contracts.Hardware;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Plugin.BlinkStickBar;
using DesktopShrine.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class BlinkStickBarTests
{
    [Fact]
    public void DefaultsToFortyEightLeds()
    {
        var settings = BlinkStickBarSettings.FromConfiguration(
            new ConfigurationBuilder().Build());

        Assert.Equal(48, settings.LedCount);
    }

    [Fact]
    public async Task JsonConfigurationReloadsWithoutRestartingPlugin()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"desktop-shrine-blinkstick-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var fileName = Path.Combine(directory, "blinkstick-bar.json");
        await File.WriteAllTextAsync(
            fileName,
            """
            {
              "LedCount": 24,
              "DataChannel": 0,
              "ExternalPower": true,
              "AnimationFramesPerSecond": 60
            }
            """,
            TestContext.Current.CancellationToken);

        var hardware = new RecordingBlinkStickHardware();
        var plugin = new BlinkStickBarPlugin(() => hardware);
        PluginContext? context = null;
        var started = false;
        try
        {
            var provider = new PluginConfigurationProvider(
                new ConfigurationBuilder().Build(),
                Options.Create(new DesktopShrineOptions
                {
                    PluginConfigurationDirectory = directory
                }),
                NullLogger<PluginConfigurationProvider>.Instance);
            context = new PluginContext(
                "blinkstick-bar",
                provider.GetConfiguration("blinkstick-bar"),
                NullLoggerFactory.Instance,
                new NoOpPublisher(),
                new NoOpSubscriber(),
                inputProfile: null);

            await plugin.InitialiseAsync(
                context,
                TestContext.Current.CancellationToken);
            await plugin.StartAsync(TestContext.Current.CancellationToken);
            started = true;
            await WaitUntilAsync(
                () => hardware.HasFrame(channel: 0, byteCount: 24 * 4));

            var replacement = Path.Combine(directory, "replacement.json");
            await File.WriteAllTextAsync(
                replacement,
                """
                {
                  "LedCount": 48,
                  "DataChannel": 2,
                  "ExternalPower": true,
                  "AnimationFramesPerSecond": 60
                }
                """,
                TestContext.Current.CancellationToken);
            File.Move(replacement, fileName, overwrite: true);

            await WaitUntilAsync(
                () => hardware.HasFrame(channel: 2, byteCount: 48 * 4));
            Assert.Equal(1, hardware.ConnectCount);
        }
        finally
        {
            if (started)
                await plugin.StopAsync(CancellationToken.None);
            await plugin.DisposeAsync();
            context?.Dispose();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        Assert.True(hardware.WasClearedBeforeDispose);
    }

    [Fact]
    public async Task NormalStartupIsUnmutedAndShutdownMuteCannotBeOverridden()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"desktop-shrine-blackout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var fileName = Path.Combine(directory, "blinkstick-bar.json");
        await File.WriteAllTextAsync(
            fileName,
            """
            {
              "LedCount": 24,
              "DataChannel": 1,
              "ExternalPower": true,
              "Brightness": 0.37,
              "Gamma": 1.7,
              "IdleBrightness": 0.5,
              "AnimationFramesPerSecond": 60
            }
            """,
            TestContext.Current.CancellationToken);

        var hardware = new RecordingBlinkStickHardware();
        var plugin = new BlinkStickBarPlugin(() => hardware);
        PluginContext? context = null;
        var started = false;
        try
        {
            var provider = new PluginConfigurationProvider(
                new ConfigurationBuilder().Build(),
                Options.Create(new DesktopShrineOptions
                {
                    PluginConfigurationDirectory = directory
                }),
                NullLogger<PluginConfigurationProvider>.Instance);
            context = new PluginContext(
                "blinkstick-bar",
                provider.GetConfiguration("blinkstick-bar"),
                NullLoggerFactory.Instance,
                new NoOpPublisher(),
                new NoOpSubscriber(),
                inputProfile: null);

            await plugin.InitialiseAsync(
                context,
                TestContext.Current.CancellationToken);
            await plugin.StartAsync(TestContext.Current.CancellationToken);
            started = true;
            await WaitUntilAsync(() => hardware.HasLitFrame);

            plugin.MuteOutputForShutdown();
            hardware.ResetFrames();
            plugin.BlackoutForShutdown();
            plugin.BlackoutForShutdown();

            await plugin.StartAsync(TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);

            Assert.Equal(1, hardware.FrameCount);
            Assert.True(hardware.AllFramesAreBlack);
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    fileName,
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                0.37,
                document.RootElement.GetProperty("Brightness").GetDouble(),
                precision: 2);
        }
        finally
        {
            if (started)
                await plugin.StopAsync(CancellationToken.None);
            await plugin.DisposeAsync();
            context?.Dispose();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        Assert.True(hardware.WasClearedBeforeDispose);
    }

    [Theory]
    [InlineData(0.50f)]
    [InlineData(0.25f)]
    [InlineData(0.10f)]
    public void BrightnessProportionallyScalesGammaShapedOutput(
        float brightness)
    {
        float[] inputs = [0.05f, 0.10f, 0.25f, 0.50f, 0.75f, 1.00f];
        var full = new LedOutputTransform(TestSettings(24) with
        {
            Gamma = 1.6f,
            Brightness = 1
        });
        var scaled = new LedOutputTransform(TestSettings(24) with
        {
            Gamma = 1.6f,
            Brightness = brightness
        });

        foreach (var input in inputs)
        {
            Assert.Equal(
                full.ApplyNormalized(input) * brightness,
                scaled.ApplyNormalized(input),
                precision: 6);
        }
    }

    [Fact]
    public void BrightnessEndpointsAreZeroAndUnattenuated()
    {
        float[] inputs = [0.05f, 0.10f, 0.25f, 0.50f, 0.75f, 1.00f];
        var off = new LedOutputTransform(TestSettings(24) with
        {
            Gamma = 1.6f,
            Brightness = 0
        });
        var full = new LedOutputTransform(TestSettings(24) with
        {
            Gamma = 1.6f,
            Brightness = 1
        });

        foreach (var input in inputs)
        {
            Assert.Equal(0, off.ApplyNormalized(input));
            Assert.Equal(
                MathF.Pow(input, 1.6f),
                full.ApplyNormalized(input),
                precision: 6);
        }
    }

    [Fact]
    public void OutputTransformKeepsBrightnessAndHardwareLimitSeparate()
    {
        byte[] effectFrame = [128, 128, 128, 128];
        var settings = TestSettings(24) with
        {
            Gamma = 1,
            Brightness = 0.5f,
            ExternalPower = false,
            UsbCurrentMa = 600,
            PixelMaximumMa = 50
        };

        var output = new LedOutputTransform(settings).Apply(effectFrame);

        Assert.Equal(0.5f, settings.Brightness);
        Assert.Equal(0.5f, settings.HardwareOutputLimit);
        Assert.All(output, value => Assert.InRange(value, (byte)31, (byte)33));
    }

    [Fact]
    public void ObsoleteCompensationConfigurationIsIgnored()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PerceptualCompensationEnabled"] = "true",
                ["PerceptualCompensationStrength"] = "not-a-number",
                ["PerceptualCompensationMinimum"] = "-100",
                ["PerceptualCompensationMaximum"] = "100",
                ["PerceptualCompensationSmoothingSeconds"] = "0",
                ["PerceptualCompensationMinimumLuminance"] = "1"
            })
            .Build();

        var settings = BlinkStickBarSettings.FromConfiguration(configuration);

        Assert.True(BlinkStickBarSettings.Validate(settings).IsValid);
        Assert.DoesNotContain(
            settings.GetType().GetProperties(),
            property => property.Name.StartsWith(
                "PerceptualCompensation",
                StringComparison.Ordinal));
    }

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(condition(), "The expected BlinkStick frame was not sent.");
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

    private sealed class RecordingBlinkStickHardware : IBlinkStickHardware
    {
        private readonly object gate = new();
        private readonly List<SentFrame> frames = [];
        private bool connected;
        private int connectCount;
        private bool wasClearedBeforeDispose;

        public bool IsConnected
        {
            get
            {
                lock (gate)
                    return connected;
            }
        }

        public int ConnectCount
        {
            get
            {
                lock (gate)
                    return connectCount;
            }
        }

        public bool WasClearedBeforeDispose
        {
            get
            {
                lock (gate)
                    return wasClearedBeforeDispose;
            }
        }

        public bool HasLitFrame
        {
            get
            {
                lock (gate)
                    return frames.Any(frame => frame.IsLit);
            }
        }

        public int FrameCount
        {
            get
            {
                lock (gate)
                    return frames.Count;
            }
        }

        public bool AllFramesAreBlack
        {
            get
            {
                lock (gate)
                    return frames.All(frame => !frame.IsLit);
            }
        }

        public bool Connect()
        {
            lock (gate)
            {
                connected = true;
                connectCount++;
                return true;
            }
        }

        public void Send(byte channel, byte[] grbwFrame)
        {
            lock (gate)
            {
                if (!connected)
                    throw new InvalidOperationException("The test BlinkStick is disconnected.");
                frames.Add(new(
                    channel,
                    grbwFrame.Length,
                    grbwFrame.Any(value => value > 0)));
            }
        }

        public void TurnOff(byte channel, int byteCount) =>
            Send(channel, new byte[byteCount]);

        public bool HasFrame(byte channel, int byteCount)
        {
            lock (gate)
            {
                return frames.Any(frame =>
                    frame.Channel == channel
                    && frame.ByteCount == byteCount);
            }
        }

        public void ResetFrames()
        {
            lock (gate)
                frames.Clear();
        }

        public void Dispose()
        {
            lock (gate)
            {
                wasClearedBeforeDispose = frames.LastOrDefault()?.IsLit == false;
                connected = false;
            }
        }

        private sealed record SentFrame(
            byte Channel,
            int ByteCount,
            bool IsLit);
    }

    private sealed class NoOpPublisher : IPluginPublisher
    {
        public ValueTask PublishAsync<T>(
            string providedPortId,
            T payload,
            CancellationToken cancellationToken = default)
            where T : IShrineMessage => ValueTask.CompletedTask;
    }

    private sealed class NoOpSubscriber : IPluginSubscriber
    {
        private readonly NoOpRouteMonitor route = new();

        public IAsyncDisposable Subscribe<T>(
            string requiredPortId,
            Func<MessageEnvelope<T>, CancellationToken, ValueTask> handler)
            where T : IShrineMessage => new NoOpSubscription();

        public IInputRouteMonitor ObserveRoute(string requiredPortId) => route;
    }

    private sealed class NoOpRouteMonitor : IInputRouteMonitor
    {
        public InputRouteSnapshot Current { get; } = new()
        {
            Consumer = new("blinkstick-bar", "media"),
            IsExclusive = true,
            Providers = []
        };

        public event EventHandler<ConfigurationChangedEventArgs<InputRouteSnapshot>>? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class NoOpSubscription : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
