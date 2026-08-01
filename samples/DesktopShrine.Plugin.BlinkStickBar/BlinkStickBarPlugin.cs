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
    private IBlinkStickHardware? hardware;
    private BlinkStickBarSettings? settings;
    private HardwareWaveRenderer? hardwareRenderer;
    private AudioSpectrumRenderer? audioRenderer;
    private ILogger<BlinkStickBarPlugin>? logger;
    private IInputRouteMonitor? mediaRoute;
    private bool mediaActive;
    private CancellationTokenSource? animationCancellation;
    private Task? animationTask;
    private long reconnectAfter;
    private bool disposed;

    public PluginDescriptor Descriptor { get; } = new()
    {
        Id = "blinkstick-bar",
        Name = "BlinkStick Hardware Visualiser",
        Version = new(2, 1, 0),
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
        settings = BlinkStickBarSettings.FromConfiguration(
            context.Configuration);
        var validation = BlinkStickBarSettings.Validate(settings);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                string.Join(" ", validation.Errors));

        hardwareRenderer = new(settings);
        audioRenderer = new(settings);
        hardware = new BlinkStickHardware();
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
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            TryConnect();
            animationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(token);
            animationTask = AnimateAsync(animationCancellation.Token);
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await StopAnimationAsync();
        lock (gate)
        {
            if (hardware?.IsConnected == true)
            {
                hardware.TurnOff(
                    (byte)settings!.DataChannel,
                    settings.LedCount * 4);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        await StopAnimationAsync();
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
            hardwareRenderer!.Update(envelope.Payload);
        return ValueTask.CompletedTask;
    }

    private ValueTask UpdateAudioAsync(
        MessageEnvelope<AudioSpectrumFrame> envelope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
            audioRenderer!.Update(envelope.Payload);
        return ValueTask.CompletedTask;
    }

    private ValueTask UpdatePaletteAsync(
        MessageEnvelope<MediaColourPalette> envelope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
            audioRenderer!.Update(envelope.Payload);
        return ValueTask.CompletedTask;
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
        using var timer = new PeriodicTimer(settings!.AnimationInterval);
        var previous = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
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
            var frame = mediaActive
                ? audioRenderer!.Render()
                : hardwareRenderer!.Render(
                    elapsed,
                    DateTimeOffset.UtcNow);
            hardware!.Send((byte)settings!.DataChannel, frame);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            logger!.LogWarning(
                exception,
                "BlinkStick frame delivery failed; the device will be reconnected");
            hardware!.Dispose();
            hardware = new BlinkStickHardware();
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
}
