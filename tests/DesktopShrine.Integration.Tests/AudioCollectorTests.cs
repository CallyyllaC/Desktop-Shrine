using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Audio;
using DesktopShrine.Plugin.AudioCollector;
using DesktopShrine.Plugin.BlinkStickBar;
using DesktopShrine.Plugin.GOverlay;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class AudioCollectorTests
{
    [Fact]
    public void EndpointSettingsDescribeLoopbackDefault()
    {
        var settings = AudioEndpointSettings.FromConfiguration(
            new ConfigurationBuilder().Build());

        Assert.True(settings.IsLoopback);
        Assert.Equal("default", settings.Device);
        Assert.True(AudioEndpointSettings.Validate(settings).IsValid);
    }

    [Fact]
    public void EndpointSettingsAcceptAnExplicitInputDevice()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CaptureMode"] = "input",
                ["Device"] = "endpoint-id"
            })
            .Build();

        var settings = AudioEndpointSettings.FromConfiguration(configuration);

        Assert.False(settings.IsLoopback);
        Assert.Equal("endpoint-id", settings.Device);
        Assert.True(AudioEndpointSettings.Validate(settings).IsValid);
    }

    [Fact]
    public void StableEndpointIdIsPreferredOverDisplayName()
    {
        AudioDeviceCandidate[] devices =
        [
            new("endpoint-a", "Speakers"),
            new("endpoint-b", "Headphones")
        ];

        var selected = AudioDeviceSelection.Resolve(
            "endpoint-b",
            "endpoint-a",
            devices);

        Assert.Equal("endpoint-b", selected.SelectedId);
        Assert.False(selected.UsedDefaultFallback);
        Assert.False(selected.UsedLegacyDisplayName);
    }

    [Fact]
    public void MissingSavedEndpointTemporarilyFallsBackWithoutChangingPreference()
    {
        const string savedPreference = "disconnected-endpoint";

        var selected = AudioDeviceSelection.Resolve(
            savedPreference,
            "current-default",
            [new("current-default", "Speakers")]);

        Assert.Equal("current-default", selected.SelectedId);
        Assert.True(selected.UsedDefaultFallback);
        Assert.Equal("disconnected-endpoint", savedPreference);
    }

    [Fact]
    public void DefaultSelectionFollowsAChangedWindowsDefault()
    {
        var selected = AudioDeviceSelection.Resolve(
            "default",
            "new-default-id",
            [new("new-default-id", "New speakers")]);

        Assert.Equal("new-default-id", selected.SelectedId);
        Assert.False(selected.UsedDefaultFallback);
    }

    [Fact]
    public async Task EndpointSelectionReloadsWithoutRestartingPlugin()
    {
        var live = new TestLiveConfiguration<AudioEndpointSettings>(
            new("loopback", "default"));
        var plugin = new AudioCollectorPlugin();
        try
        {
            await plugin.InitialiseAsync(
                new EndpointTestContext(live),
                TestContext.Current.CancellationToken);

            live.Update(new("input", "microphone-id"));

            Assert.Equal("input", plugin.CurrentEndpointSettings.CaptureMode);
            Assert.Equal("microphone-id", plugin.CurrentEndpointSettings.Device);
        }
        finally
        {
            await plugin.DisposeAsync();
        }
    }

    [Fact]
    public void LiveFftFrameDrivesBothPhysicalVisualisers()
    {
        const int fftSize = 2_048;
        const int sampleRate = 48_000;
        const int toneBin = 19;
        var signal = Enumerable.Range(0, fftSize)
            .Select(index => (float)(0.05 * Math.Sin(
                2 * Math.PI * toneBin * index / fftSize)))
            .ToArray();
        var processor = new AudioSignalProcessor(fftSize, useSimd: false);
        processor.Append(signal);
        var processed = processor.Snapshot(160);
        var now = DateTimeOffset.UtcNow;
        var frame = new AudioSpectrumFrame
        {
            CapturedAt = now,
            Sequence = 1,
            SampleRate = sampleRate,
            FftSize = fftSize,
            Spectrum = processed.Spectrum,
            Waveform = processed.Waveform
        };

        var waterfall = new GOverlayWaterfallAggregator(
            new GOverlayWaterfallOptions());
        waterfall.Add(frame);
        var waterfallOutput = waterfall.Consume(now);

        var settings = BlinkStickBarSettings.FromConfiguration(
            new ConfigurationBuilder().Build()) with
        {
            UsbCurrentMa = 10_000,
            ExternalPower = true,
            Brightness = 1,
            Attack = 1,
            PeakRise = 1
        };
        var blinkStick = new AudioSpectrumRenderer(settings);
        blinkStick.Update(frame);
        var blinkStickOutput = blinkStick.Render();

        Assert.True(waterfallOutput.HasColumn);
        Assert.Contains(waterfallOutput.Values, value => value > 0);
        Assert.Contains(blinkStickOutput, value => value > 0);
    }

    [Fact]
    public void StereoSamplesAreMixedToMono()
    {
        var mono = AudioSignalProcessor.MixToMono([1f, -1f, 0.5f, 0.25f], 2);

        Assert.Equal([0f, 0.375f], mono);
    }

    [Fact]
    public void SpectrumUsesNativeRealFftBinCountAndFindsTone()
    {
        const int fftSize = 2_048;
        const int expectedBin = 64;
        var signal = Enumerable.Range(0, fftSize)
            .Select(index => (float)Math.Sin(2 * Math.PI * expectedBin * index / fftSize))
            .ToArray();
        var processor = new AudioSignalProcessor(fftSize);
        processor.Append(signal);

        var frame = processor.Snapshot(160);
        var strongestBin = Array.IndexOf(frame.Spectrum, frame.Spectrum.Max());

        Assert.Equal((fftSize / 2) + 1, frame.Spectrum.Length);
        Assert.Equal(160, frame.Waveform.Length);
        Assert.Equal(expectedBin, strongestBin);
    }

    [Fact]
    public void SimdAndScalarFftPathsProduceEquivalentSpectrum()
    {
        const int fftSize = 2_048;
        var signal = Enumerable.Range(0, fftSize)
            .Select(index => (float)(
                (0.7 * Math.Sin(2 * Math.PI * 37 * index / fftSize))
                + (0.2 * Math.Sin(2 * Math.PI * 311 * index / fftSize))))
            .ToArray();
        var simd = new AudioSignalProcessor(fftSize, useSimd: true);
        var scalar = new AudioSignalProcessor(fftSize, useSimd: false);
        simd.Append(signal);
        scalar.Append(signal);

        var simdFrame = simd.Snapshot(160);
        var scalarFrame = scalar.Snapshot(160);

        for (var index = 0; index < simdFrame.Spectrum.Length; index++)
            Assert.Equal(scalarFrame.Spectrum[index], simdFrame.Spectrum[index], 3);
    }

    [Fact]
    public void FloatCaptureDataIsDecodedAndMixed()
    {
        var bytes = new byte[4 * sizeof(float)];
        Buffer.BlockCopy(new[] { 1f, -1f, 0.5f, 0.25f }, 0, bytes, 0, bytes.Length);

        var mono = AudioSampleDecoder.DecodeToMono(bytes, 2, 32, AudioSampleEncoding.Float);

        Assert.Equal([0f, 0.375f], mono);
    }

    [Fact]
    public void FftSizeMustBePowerOfTwo()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioSignalProcessor(1_000));
    }

    private sealed class TestLiveConfiguration<TConfig>(TConfig current) :
        ILiveConfiguration<TConfig>
        where TConfig : notnull
    {
        public TConfig Current { get; private set; } = current;

        public event EventHandler<ConfigurationChangedEventArgs<TConfig>>? Changed;

        public void Update(TConfig value)
        {
            var previous = Current;
            Current = value;
            Changed?.Invoke(
                this,
                new ConfigurationChangedEventArgs<TConfig>(previous, value));
        }
    }

    private sealed class EndpointTestContext(
        TestLiveConfiguration<AudioEndpointSettings> endpointSettings) :
        IPluginContext
    {
        public string PluginId => "audio-collector";
        public IConfiguration Configuration { get; } =
            new ConfigurationBuilder().Build();
        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
        public IPluginPublisher Publisher => throw new NotSupportedException();
        public IPluginSubscriber Subscriber => throw new NotSupportedException();
        public ILiveConfiguration<OutputInputProfile>? InputProfile => null;
        public IPluginConfigurationEditor? ConfigurationEditor => null;

        public ILiveConfiguration<TConfig> ObserveConfiguration<TConfig>(
            Func<IConfiguration, TConfig> snapshotFactory,
            Func<TConfig, ConfigurationValidationResult>? validator = null)
            where TConfig : notnull
        {
            _ = snapshotFactory;
            _ = validator;
            return (ILiveConfiguration<TConfig>)(object)endpointSettings;
        }
    }
}
