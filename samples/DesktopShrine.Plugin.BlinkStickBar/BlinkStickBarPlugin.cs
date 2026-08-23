using System.Diagnostics;
using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Audio;
using DesktopShrine.Contracts.Hardware;
using DesktopShrine.Contracts.Media;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Plugin.BlinkStickBar;

public sealed class BlinkStickBarPlugin : IOutputPlugin
{
    private const string MediaPortId = "media";
    private const string PalettePortId = "palette";
    private const string AudioPortId = "audio";
    private const string HardwarePortId = "hardware";
    private readonly object gate = new();
    private readonly List<IAsyncDisposable> subscriptions = [];
    private readonly Func<IBlinkStickHardware> hardwareFactory;
    private IBlinkStickHardware? hardware;
    private ILiveConfiguration<BlinkStickBarSettings>? liveSettings;
    private BlinkStickBarSettings? settings;
    private HardwareWaveRenderer? hardwareRenderer;
    private AudioSpectrumRenderer? audioRenderer;
    private LedOutputTransform? outputTransform;
    private HardwareMonitorState? latestHardwareState;
    private AudioSpectrumFrame? latestAudioFrame;
    private MediaColourPalette? latestPalette;
    private ILogger<BlinkStickBarPlugin>? logger;
    private IInputRouteMonitor? mediaRoute;
    private bool mediaActive;
    private CancellationTokenSource? animationCancellation;
    private Task? animationTask;
    private long reconnectAfter;
    private bool disposed;
    private bool shutdownClearSent;

    public BlinkStickBarPlugin() : this(() => new BlinkStickHardware())
    {
    }

    internal BlinkStickBarPlugin(
        Func<IBlinkStickHardware> hardwareFactory)
    {
        this.hardwareFactory = hardwareFactory
            ?? throw new ArgumentNullException(nameof(hardwareFactory));
    }

    public PluginDescriptor Descriptor { get; } = new()
    {
        Id = "blinkstick-bar",
        Name = "BlinkStick Hardware Visualiser",
        Version = new(2, 2, 0),
        Description =
            "Renders media audio when active, falling back to centre-out GPU and CPU telemetry waves.",
        SupportedPlatforms = ["windows"]
    };

    public IReadOnlyCollection<RequiredPortDescriptor> RequiredPorts { get; } =
    [
        new()
        {
            PortId = MediaPortId,
            DisplayName = "Active media",
            Requirement = new(
                "desktop-shrine.media.now-playing",
                VersionRange.Between(new(1, 1, 0), new(2, 0, 0))),
            IsRequired = false
        },
        new()
        {
            PortId = PalettePortId,
            DisplayName = "Artwork colour palette",
            Requirement = new(
                "desktop-shrine.media.colour-palette",
                VersionRange.Between(new(1, 0, 0), new(2, 0, 0))),
            IsRequired = false
        },
        new()
        {
            PortId = AudioPortId,
            DisplayName = "Audio spectrum",
            Requirement = new(
                "desktop-shrine.audio.spectrum",
                VersionRange.Between(new(1, 0, 0), new(2, 0, 0))),
            IsRequired = false
        },
        new()
        {
            PortId = HardwarePortId,
            DisplayName = "Hardware monitor",
            Requirement = new(
                "desktop-shrine.hardware.monitor",
                VersionRange.Between(new(1, 0, 0), new(2, 0, 0)))
        }
    ];

    public ValueTask InitialiseAsync(
        IPluginContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        logger = context.LoggerFactory.CreateLogger<BlinkStickBarPlugin>();
        liveSettings = context.ObserveConfiguration(
            BlinkStickBarSettings.FromConfiguration,
            BlinkStickBarSettings.Validate);
        settings = liveSettings.Current;

        hardwareRenderer = new(settings);
        audioRenderer = new(settings);
        outputTransform = new(settings);
        hardware = hardwareFactory();
        mediaRoute = context.Subscriber.ObserveRoute(MediaPortId);
        mediaRoute.Changed += OnMediaRouteChanged;
        mediaActive = mediaRoute.Current.SelectedProvider is not null;
        subscriptions.Add(
            context.Subscriber.Subscribe<MediaColourPalette>(
                PalettePortId,
                UpdatePaletteAsync));
        subscriptions.Add(
            context.Subscriber.Subscribe<AudioSpectrumFrame>(
                AudioPortId,
                UpdateAudioAsync));
        subscriptions.Add(
            context.Subscriber.Subscribe<HardwareMonitorState>(
                HardwarePortId,
                UpdateHardwareAsync));
        liveSettings.Changed += OnConfigurationChanged;
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            shutdownClearSent = false;
            TryConnect();
            animationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(token);
            animationTask = AnimateAsync(animationCancellation.Token);
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        _ = token;
        await StopAnimationAsync();
        lock (gate)
            ClearOutputCore();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        await StopAnimationAsync();
        if (liveSettings is not null)
        {
            liveSettings.Changed -= OnConfigurationChanged;
            liveSettings = null;
        }
        if (mediaRoute is not null)
        {
            mediaRoute.Changed -= OnMediaRouteChanged;
            mediaRoute = null;
        }
        foreach (var subscription in subscriptions)
            await subscription.DisposeAsync();
        subscriptions.Clear();
        lock (gate)
        {
            ClearOutputCore();
            hardware?.Dispose();
            hardware = null;
        }
    }

    private ValueTask UpdateHardwareAsync(
        MessageEnvelope<HardwareMonitorState> envelope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            latestHardwareState = envelope.Payload;
            hardwareRenderer!.Update(envelope.Payload);
        }
        return ValueTask.CompletedTask;
    }

    private ValueTask UpdateAudioAsync(
        MessageEnvelope<AudioSpectrumFrame> envelope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            latestAudioFrame = envelope.Payload;
            audioRenderer!.Update(envelope.Payload);
        }
        return ValueTask.CompletedTask;
    }

    private ValueTask UpdatePaletteAsync(
        MessageEnvelope<MediaColourPalette> envelope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            latestPalette = envelope.Payload;
            audioRenderer!.Update(envelope.Payload);
        }
        return ValueTask.CompletedTask;
    }

    private void OnConfigurationChanged(
        object? sender,
        ConfigurationChangedEventArgs<BlinkStickBarSettings> args)
    {
        _ = sender;
        lock (gate)
        {
            if (disposed)
                return;

            if (args.Previous.DataChannel != args.Current.DataChannel
                || args.Previous.LedCount != args.Current.LedCount)
            {
                TurnOffPreviousLayout(args.Previous);
            }

            settings = args.Current;
            hardwareRenderer = new(settings);
            audioRenderer = new(settings);
            outputTransform = new(settings);
            if (latestHardwareState is not null)
                hardwareRenderer.Update(latestHardwareState);
            if (latestPalette is not null)
                audioRenderer.Update(latestPalette);
            if (latestAudioFrame is not null)
                audioRenderer.Update(latestAudioFrame);
            reconnectAfter = 0;

            logger?.LogInformation(
                "Applied live BlinkStick configuration: channel {Channel}, {LedCount} RGBW pixels, {FrameRate:F0} FPS, effective brightness {Brightness:P0}",
                settings.DataChannel,
                settings.LedCount,
                settings.AnimationFramesPerSecond,
                settings.EffectiveBrightness);
        }
    }

    private void OnMediaRouteChanged(
        object? sender,
        ConfigurationChangedEventArgs<InputRouteSnapshot> args)
    {
        _ = sender;
        lock (gate)
        {
            mediaActive = args.Current.SelectedProvider is not null;
            logger?.LogInformation(
                "BlinkStick switched to {Mode} visualisation{Provider}",
                mediaActive ? "audio" : "hardware",
                mediaActive
                    ? $" from {args.Current.SelectedProvider!.PluginId}"
                    : string.Empty);
        }
    }

    private async Task AnimateAsync(CancellationToken token)
    {
        var previous = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                TimeSpan interval;
                lock (gate)
                    interval = settings!.AnimationInterval;
                await Task.Delay(interval, token);

                var current = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(previous, current);
                previous = current;
                lock (gate)
                    RenderAndSend(elapsed);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void RenderAndSend(TimeSpan elapsed)
    {
        if (hardware?.IsConnected != true && !TryConnect())
            return;

        try
        {
            var effectFrame = mediaActive
                ? audioRenderer!.Render()
                : hardwareRenderer!.Render(
                    elapsed,
                    DateTimeOffset.UtcNow);
            var frame = outputTransform!.Apply(effectFrame);
            hardware!.Send((byte)settings!.DataChannel, frame);
            shutdownClearSent = false;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            logger!.LogWarning(
                exception,
                "BlinkStick frame delivery failed; the device will be reconnected");
            hardware!.Dispose();
            hardware = hardwareFactory();
            reconnectAfter = Stopwatch.GetTimestamp()
                + (long)(
                    settings!.ReconnectInterval.TotalSeconds
                    * Stopwatch.Frequency);
        }
    }

    private async ValueTask StopAnimationAsync()
    {
        var cancellation = animationCancellation;
        var task = animationTask;
        animationCancellation = null;
        animationTask = null;
        if (cancellation is null)
            return;

        await cancellation.CancelAsync();
        if (task is not null)
            await task;
        cancellation.Dispose();
    }

    private void TurnOffPreviousLayout(BlinkStickBarSettings previous)
    {
        if (hardware?.IsConnected != true)
            return;

        try
        {
            hardware.TurnOff(
                (byte)previous.DataChannel,
                previous.LedCount * 4);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Could not clear the previous BlinkStick channel layout; the device will be reconnected");
            hardware.Dispose();
            hardware = hardwareFactory();
        }
    }

    private bool TryConnect()
    {
        if (hardware!.IsConnected)
            return true;
        if (Stopwatch.GetTimestamp() < reconnectAfter)
            return false;

        try
        {
            if (hardware.Connect())
            {
                hardware.TurnOff(
                    (byte)settings!.DataChannel,
                    settings.LedCount * 4);
                shutdownClearSent = false;
                logger!.LogInformation(
                    "Connected to BlinkStick Pro on channel {Channel} with {LedCount} RGBW pixels; hardware waves at {FrameRate:F0} FPS; effective brightness {Brightness:P0}",
                    settings!.DataChannel,
                    settings.LedCount,
                    settings.AnimationFramesPerSecond,
                    settings.EffectiveBrightness);
                return true;
            }
            logger!.LogWarning(
                "No BlinkStick device was found; retrying in {Delay}",
                settings!.ReconnectInterval);
        }
        catch (Exception exception)
        {
            logger!.LogWarning(
                exception,
                "Could not connect to BlinkStick; retrying in {Delay}",
                settings!.ReconnectInterval);
        }

        reconnectAfter = Stopwatch.GetTimestamp()
            + (long)(
                settings!.ReconnectInterval.TotalSeconds
                * Stopwatch.Frequency);
        return false;
    }

    private void ClearOutputCore()
    {
        if (shutdownClearSent || hardware?.IsConnected != true || settings is null)
            return;
        try
        {
            hardware.TurnOff(
                (byte)settings.DataChannel,
                settings.LedCount * 4);
            shutdownClearSent = true;
            logger?.LogInformation("Cleared BlinkStick LEDs before shutdown");
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Could not clear BlinkStick LEDs before disposing the device");
        }
    }
}
