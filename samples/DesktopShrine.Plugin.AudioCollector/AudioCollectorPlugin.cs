using System.Diagnostics;
using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Audio;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DesktopShrine.Plugin.AudioCollector;

public sealed class AudioCollectorPlugin : IInputPlugin
{
    private const string PortId = "audio-spectrum";
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private IPluginContext? context;
    private ILogger<AudioCollectorPlugin>? logger;
    private readonly object captureGate = new();
    private CancellationTokenSource? stop;
    private Task? publisher;
    private Task? deviceMonitor;
    private MMDeviceEnumerator? deviceEnumerator;
    private MMDevice? device;
    private WasapiCapture? capture;
    private AudioSignalProcessor? processor;
    private int fftSize;
    private int framesPerSecond;
    private int waveformSamples;
    private TimeSpan statusInterval;
    private TimeSpan devicePollInterval;
    private ILiveConfiguration<AudioEndpointSettings>? liveEndpointSettings;
    private AudioEndpointSettings? endpointSettings;
    private string? selectedDeviceId;
    private int currentSampleRate;
    private int captureRefreshRequested;
    private int sequence;
    private long capturedSamples;

    public PluginDescriptor Descriptor { get; } = new()
    {
        Id = "audio-collector",
        Name = "Audio Collector",
        Version = new(1, 2, 0),
        Description = "Captures Windows audio, mixes it to mono, and publishes FFT spectrum frames.",
        SupportedPlatforms = ["windows"]
    };

    public IReadOnlyCollection<ProvidedPortDescriptor> ProvidedPorts { get; } =
    [
        new()
        {
            PortId = PortId,
            DisplayName = "Audio spectrum",
            Contract = new("desktop-shrine.audio.spectrum", new(1, 0, 0))
        }
    ];

    internal AudioEndpointSettings CurrentEndpointSettings =>
        Volatile.Read(ref endpointSettings)
        ?? throw new InvalidOperationException("Audio Collector is not initialised.");

    public ValueTask InitialiseAsync(IPluginContext value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        context = value;
        logger = value.LoggerFactory.CreateLogger<AudioCollectorPlugin>();
        framesPerSecond = ReadInteger("FramesPerSecond", 50, 1, 240);
        waveformSamples = ReadInteger("WaveformSamples", 160, 2, 16_384);
        statusInterval = TimeSpan.FromSeconds(ReadInteger("StatusIntervalSeconds", 10, 0, 3_600));
        devicePollInterval = TimeSpan.FromSeconds(
            ReadInteger("DevicePollIntervalSeconds", 2, 1, 60));
        fftSize = ReadInteger("FftSize", 2_048, 2, 1 << 20);
        processor = new(fftSize);
        liveEndpointSettings = value.ObserveConfiguration(
            AudioEndpointSettings.FromConfiguration,
            AudioEndpointSettings.Validate);
        endpointSettings = liveEndpointSettings.Current;
        liveEndpointSettings.Changed += OnEndpointSettingsChanged;
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Audio Collector requires Windows 10 version 1809 or newer.");

        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        StartCapture(isRecovery: false);
        publisher = PublishFramesAsync(stop.Token);
        deviceMonitor = MonitorCaptureDeviceAsync(stop.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        if (stop is null)
            return;

        await stop.CancelAsync();
        StopCapture();
        foreach (var task in new[] { publisher, deviceMonitor })
        {
            if (task is null)
                continue;
            try { await task.WaitAsync(token); }
            catch (OperationCanceledException) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        StopCapture();
        stop?.Dispose();
        if (liveEndpointSettings is not null)
        {
            liveEndpointSettings.Changed -= OnEndpointSettingsChanged;
            liveEndpointSettings = null;
        }
        return ValueTask.CompletedTask;
    }

    private async Task PublishFramesAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / framesPerSecond));
        var lastStatusAt = Stopwatch.GetTimestamp();
        var samplesAtLastStatus = 0L;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var frame = processor!.Snapshot(waveformSamples);
                sequence = sequence == int.MaxValue ? 0 : sequence + 1;
                await context!.Publisher.PublishAsync(PortId, new AudioSpectrumFrame
                {
                    CapturedAt = DateTimeOffset.UtcNow,
                    Sequence = sequence,
                    SampleRate = Volatile.Read(ref currentSampleRate),
                    FftSize = processor.FftSize,
                    Spectrum = frame.Spectrum,
                    Waveform = frame.Waveform
                }, token);

                if (statusInterval > TimeSpan.Zero
                    && Stopwatch.GetElapsedTime(lastStatusAt) >= statusInterval)
                {
                    var captured = Interlocked.Read(ref capturedSamples);
                    LogStatus(
                        frame,
                        Volatile.Read(ref currentSampleRate),
                        captured - samplesAtLastStatus);
                    samplesAtLastStatus = captured;
                    lastStatusAt = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger!.LogError(exception, "Audio Collector publisher failed");
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        try
        {
            if (sender is not WasapiCapture source)
                return;
            if (!ReferenceEquals(source, Volatile.Read(ref capture)))
                return;
            var format = source.WaveFormat;
            var mono = AudioSampleDecoder.DecodeToMono(
                args.Buffer.AsSpan(0, args.BytesRecorded),
                format.Channels,
                format.BitsPerSample,
                GetEncoding(format));
            processor!.Append(mono);
            Interlocked.Add(ref capturedSamples, mono.Length);
        }
        catch (Exception exception)
        {
            logger!.LogError(exception, "Could not decode captured audio");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (stop?.IsCancellationRequested == true)
            return;
        if (sender is WasapiCapture source
            && !ReferenceEquals(source, Volatile.Read(ref capture)))
            return;

        Interlocked.Exchange(ref captureRefreshRequested, 1);
        if (args.Exception is not null)
        {
            logger!.LogWarning(
                args.Exception,
                "Audio capture stopped unexpectedly; automatic recovery is scheduled");
        }
        else
        {
            logger!.LogWarning(
                "Audio capture stopped; automatic recovery is scheduled");
        }
    }

    private async Task MonitorCaptureDeviceAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(devicePollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var refresh = Interlocked.Exchange(
                    ref captureRefreshRequested,
                    0) != 0;
                try
                {
                    using var probe = new MMDeviceEnumerator();
                    var endpoint = Volatile.Read(ref endpointSettings)!;
                    using var desired = FindDevice(
                        probe,
                        endpoint.IsLoopback ? DataFlow.Render : DataFlow.Capture,
                        endpoint.Device).Device;
                    lock (captureGate)
                        refresh |= !string.Equals(
                            selectedDeviceId,
                            desired.ID,
                            StringComparison.Ordinal);
                    if (refresh)
                        StartCapture(isRecovery: true);
                }
                catch (Exception exception)
                {
                    Interlocked.Exchange(ref captureRefreshRequested, 1);
                    logger!.LogWarning(
                        exception,
                        "Audio capture endpoint is unavailable; retrying in {Delay}",
                        devicePollInterval);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void StartCapture(bool isRecovery)
    {
        lock (captureGate)
        {
            StopCaptureCore();
            deviceEnumerator = new();
            var endpoint = Volatile.Read(ref endpointSettings)!;
            var flow = endpoint.IsLoopback ? DataFlow.Render : DataFlow.Capture;
            var resolution = FindDevice(
                deviceEnumerator,
                flow,
                endpoint.Device);
            device = resolution.Device;
            if (resolution.Selection.UsedDefaultFallback)
            {
                logger!.LogWarning(
                    "Configured audio endpoint {ConfiguredDevice} is unavailable; using the current default without changing the saved preference",
                    endpoint.Device);
            }
            else if (resolution.Selection.UsedLegacyDisplayName)
            {
                logger!.LogInformation(
                    "Resolved legacy audio device name {ConfiguredDevice} to endpoint ID {DeviceId}",
                    endpoint.Device,
                    resolution.Selection.SelectedId);
            }
            capture = endpoint.IsLoopback
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device);
            ValidateFormat(capture.WaveFormat);
            processor = new(fftSize);
            Volatile.Write(
                ref currentSampleRate,
                capture.WaveFormat.SampleRate);
            selectedDeviceId = device.ID;
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            logger!.LogInformation(
                isRecovery
                    ? "Audio Collector rebound to {Source} device {Device} at {SampleRate} Hz, {Channels} channels, {Bits} bit"
                    : "Audio Collector started: {Source} device {Device} at {SampleRate} Hz, {Channels} channels, {Bits} bit, SIMD {SimdStatus} ({SimdWidth} floats per vector)",
                endpoint.IsLoopback ? "desktop loopback of output" : "input",
                device.FriendlyName,
                capture.WaveFormat.SampleRate,
                capture.WaveFormat.Channels,
                capture.WaveFormat.BitsPerSample,
                processor.IsSimdEnabled ? "enabled" : "unavailable",
                processor.SimdWidth);
        }
    }

    private void StopCapture()
    {
        lock (captureGate)
            StopCaptureCore();
    }

    private void StopCaptureCore()
    {
        var previous = capture;
        capture = null;
        selectedDeviceId = null;
        if (previous is not null)
        {
            previous.DataAvailable -= OnDataAvailable;
            previous.RecordingStopped -= OnRecordingStopped;
            try { previous.StopRecording(); }
            catch { }
            previous.Dispose();
        }
        device?.Dispose();
        device = null;
        deviceEnumerator?.Dispose();
        deviceEnumerator = null;
    }

    private void OnEndpointSettingsChanged(
        object? sender,
        ConfigurationChangedEventArgs<AudioEndpointSettings> args)
    {
        _ = sender;
        if (args.Previous == args.Current)
            return;

        Volatile.Write(ref endpointSettings, args.Current);
        if (stop?.IsCancellationRequested != false)
            return;

        try
        {
            StartCapture(isRecovery: true);
            Interlocked.Exchange(ref captureRefreshRequested, 0);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref captureRefreshRequested, 1);
            logger!.LogWarning(
                exception,
                "Could not switch audio capture endpoint immediately; automatic recovery is scheduled");
        }
    }

    private void LogStatus(ProcessedAudio frame, int sampleRate, long samplesSinceLastStatus)
    {
        var peak = 0f;
        var squareSum = 0d;
        foreach (var sample in frame.Waveform)
        {
            peak = Math.Max(peak, Math.Abs(sample));
            squareSum += sample * sample;
        }

        var rms = frame.Waveform.Length == 0
            ? 0
            : Math.Sqrt(squareSum / frame.Waveform.Length);
        var strongestBin = 0;
        for (var index = 1; index < frame.Spectrum.Length; index++)
        {
            if (frame.Spectrum[index] > frame.Spectrum[strongestBin])
                strongestBin = index;
        }
        var strongestFrequency = strongestBin * sampleRate / (double)processor!.FftSize;

        logger!.LogInformation(
            "Audio Collector status: sequence {Sequence}, captured {CapturedSamples} new mono samples, {Activity}, peak {Peak:F4}, RMS {Rms:F4}, strongest bin {StrongestFrequency:F0} Hz",
            sequence,
            samplesSinceLastStatus,
            peak >= 0.0001f ? "signal present" : "quiet",
            peak,
            rms,
            strongestFrequency);
    }

    private static ResolvedDevice FindDevice(
        MMDeviceEnumerator enumerator,
        DataFlow flow,
        string? configuredDevice)
    {
        var defaultDevice = enumerator.GetDefaultAudioEndpoint(
            flow,
            Role.Multimedia);
        MMDevice[] devices;
        try
        {
            devices = enumerator.EnumerateAudioEndPoints(
                    flow,
                    DeviceState.Active)
                .ToArray();
        }
        catch
        {
            return new(
                defaultDevice,
                new(defaultDevice.ID, true, false));
        }

        AudioDeviceResolution selection;
        try
        {
            selection = AudioDeviceSelection.Resolve(
                configuredDevice,
                defaultDevice.ID,
                devices.Select(candidate => new AudioDeviceCandidate(
                        candidate.ID,
                        candidate.FriendlyName))
                    .ToArray());
        }
        catch
        {
            foreach (var candidate in devices)
                candidate.Dispose();
            return new(
                defaultDevice,
                new(defaultDevice.ID, true, false));
        }

        var selected = devices.FirstOrDefault(candidate =>
            candidate.ID.Equals(
                selection.SelectedId,
                StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            foreach (var candidate in devices)
                candidate.Dispose();
            return new(defaultDevice, selection);
        }

        foreach (var candidate in devices)
        {
            if (!ReferenceEquals(candidate, selected))
                candidate.Dispose();
        }
        defaultDevice.Dispose();
        return new(selected, selection);
    }

    private sealed record ResolvedDevice(
        MMDevice Device,
        AudioDeviceResolution Selection);

    private static void ValidateFormat(WaveFormat format) => _ = GetEncoding(format);

    private static AudioSampleEncoding GetEncoding(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            return AudioSampleEncoding.Float;
        if (format.Encoding == WaveFormatEncoding.Pcm)
            return AudioSampleEncoding.Pcm;
        if (format is WaveFormatExtensible extensible && extensible.SubFormat == FloatSubFormat)
            return AudioSampleEncoding.Float;
        if (format is WaveFormatExtensible pcmExtensible && pcmExtensible.SubFormat == PcmSubFormat)
            return AudioSampleEncoding.Pcm;
        throw new NotSupportedException($"Unsupported capture format: {format}.");
    }

    private int ReadInteger(string key, int defaultValue, int minimum, int maximum)
    {
        var raw = context!.Configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"Audio Collector setting {key} must be between {minimum} and {maximum}.");
        return value;
    }
}
