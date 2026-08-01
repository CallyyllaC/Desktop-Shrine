using System.Diagnostics;
using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Audio;
using DesktopShrine.Contracts.Media;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Plugin.ConsoleDisplay;

public sealed class ConsoleDisplayPlugin : IOutputPlugin
{
    private const string MediaPortId = "media-display";
    private const string AudioPortId = "audio-display";
    private readonly List<IAsyncDisposable> subscriptions = [];
    private ILogger<ConsoleDisplayPlugin>? logger;
    private ConsoleDisplaySettings settings = new();
    private long lastAudioStatusAt;

    public PluginDescriptor Descriptor { get; } = new()
    {
        Id = "console-display",
        Name = "Console Display",
        Version = new(1, 0, 0)
    };

    public IReadOnlyCollection<RequiredPortDescriptor> RequiredPorts { get; } =
    [
        new()
        {
            PortId = MediaPortId,
            DisplayName = "Media display",
            Requirement = new(
                "desktop-shrine.media.now-playing",
                VersionRange.Between(new(1, 0, 0), new(2, 0, 0)))
        },
        new()
        {
            PortId = AudioPortId,
            DisplayName = "Audio activity display",
            Requirement = new(
                "desktop-shrine.audio.spectrum",
                VersionRange.Between(new(1, 0, 0), new(2, 0, 0)))
        }
    ];

    public ValueTask InitialiseAsync(IPluginContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        logger = context.LoggerFactory.CreateLogger<ConsoleDisplayPlugin>();
        settings = ConsoleDisplaySettings.FromConfiguration(
            context.Configuration);
        subscriptions.Add(context.Subscriber.Subscribe<NowPlayingState>(
            MediaPortId,
            DisplayMediaAsync));
        subscriptions.Add(context.Subscriber.Subscribe<AudioSpectrumFrame>(
            AudioPortId,
            DisplayAudioAsync));
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in subscriptions)
            await subscription.DisposeAsync();
        subscriptions.Clear();
    }

    private ValueTask DisplayMediaAsync(
        MessageEnvelope<NowPlayingState> envelope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!settings.ShowMediaUpdates)
            return ValueTask.CompletedTask;

        if (envelope.Payload.IsAvailable)
        {
            logger!.LogInformation(
                "Now playing: {Title} — {Artist} [{Status}] from {SourceApp}",
                envelope.Payload.Title ?? "(untitled)",
                envelope.Payload.Artist ?? "(unknown artist)",
                envelope.Payload.Status,
                envelope.Payload.SourceAppUserModelId);
        }
        else
        {
            logger!.LogInformation("Nothing is currently playing");
        }
        return ValueTask.CompletedTask;
    }

    private ValueTask DisplayAudioAsync(
        MessageEnvelope<AudioSpectrumFrame> envelope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!settings.ShowAudioStatus)
            return ValueTask.CompletedTask;

        var now = Stopwatch.GetTimestamp();
        if (lastAudioStatusAt != 0
            && Stopwatch.GetElapsedTime(lastAudioStatusAt, now)
                < settings.AudioStatusInterval)
            return ValueTask.CompletedTask;
        lastAudioStatusAt = now;

        var frame = envelope.Payload;
        var peak = 0f;
        var squareSum = 0d;
        foreach (var sample in frame.Waveform)
        {
            peak = Math.Max(peak, Math.Abs(sample));
            squareSum += sample * sample;
        }

        var rms = frame.Waveform.Count == 0
            ? 0
            : Math.Sqrt(squareSum / frame.Waveform.Count);
        var rmsDb = 20 * Math.Log10(Math.Max(rms, 0.000001));
        var meterLength = settings.AudioMeterWidth;
        var filled = Math.Clamp(
            (int)Math.Round(
                (rmsDb - settings.AudioMeterFloorDb)
                / -settings.AudioMeterFloorDb
                * meterLength),
            0,
            meterLength);
        var meter = new string('#', filled) + new string('-', meterLength - filled);

        var strongestBin = 0;
        for (var index = 1; index < frame.Spectrum.Count; index++)
        {
            if (frame.Spectrum[index] > frame.Spectrum[strongestBin])
                strongestBin = index;
        }
        var strongestFrequency = strongestBin * frame.SampleRate / (double)frame.FftSize;

        logger!.LogInformation(
            "Audio: [{Meter}] {Activity}, peak {Peak:F4}, RMS {Rms:F4} ({RmsDb:F1} dBFS), strongest {StrongestFrequency:F0} Hz, sequence {Sequence}",
            meter,
            peak >= settings.SignalThreshold ? "signal present" : "quiet",
            peak,
            rms,
            rmsDb,
            strongestFrequency,
            frame.Sequence);
        return ValueTask.CompletedTask;
    }

}
