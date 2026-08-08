using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Audio;
using DesktopShrine.Contracts.Hardware;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Plugin.GOverlay.Layout;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Plugin.GOverlay;

public sealed class GOverlayPlugin : IOutputPlugin
{
    private const string MediaPortId = "media";
    private const string PalettePortId = "palette";
    private const string AudioPortId = "audio";
    private const string HardwarePortId = "hardware";
    private readonly object gate = new();
    private readonly List<IAsyncDisposable> subscriptions = [];
    private GOverlayStateBuilder? state;
    private ILogger<GOverlayPlugin>? logger;
    private GOverlaySettings settings = new();
    private GOverlayBridgeServer? bridge;
    private IInputRouteMonitor? mediaRoute;
    private bool disposed;

    public PluginDescriptor Descriptor { get; } = new()
    {
        Id = "goverlay",
        Name = "GOverlay Dashboard",
        Version = new(0, 1, 0),
        Description =
            "Renders media or a centre-out hardware monitor on LCDSysInfo.",
        SupportedPlatforms = ["windows"]
    };

    public IReadOnlyCollection<RequiredPortDescriptor> RequiredPorts { get; } =
    [
        new()
        {
            PortId = MediaPortId,
            DisplayName = "Now Playing media",
            Requirement = new(
                "desktop-shrine.media.now-playing",
                VersionRange.Between(new(1, 1, 0), new(2, 0, 0)))
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
                VersionRange.Between(new(1, 0, 0), new(2, 0, 0))),
            IsRequired = false
        }
    ];

    public ValueTask InitialiseAsync(
        IPluginContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        logger = context.LoggerFactory.CreateLogger<GOverlayPlugin>();
        settings = GOverlaySettings.FromConfiguration(context.Configuration);
        state = new(
            settings.Waterfall,
            settings.WaterfallColumnWidth)
        {
            FontName = settings.FontName
        };
        bridge = new(
            settings.PipeName,
            settings.UpdateInterval,
            logger,
            PrepareDisplayState);
        mediaRoute = context.Subscriber.ObserveRoute(MediaPortId);
        mediaRoute.Changed += OnMediaRouteChanged;
        state.UpdateMediaRoute(
            mediaRoute.Current.SelectedProvider?.PluginId);
        subscriptions.Add(context.Subscriber.Subscribe<NowPlayingState>(
            MediaPortId,
            UpdateMediaAsync));
        subscriptions.Add(context.Subscriber.Subscribe<MediaColourPalette>(
            PalettePortId,
            UpdatePaletteAsync));
        subscriptions.Add(context.Subscriber.Subscribe<AudioSpectrumFrame>(
            AudioPortId,
            UpdateAudioAsync));
        subscriptions.Add(context.Subscriber.Subscribe<HardwareMonitorState>(
            HardwarePortId,
            UpdateHardwareAsync));
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var devices = WindowsGOverlayDeviceDetector.FindConnected();
        state!.ConfigureArtwork(settings.DontUseDrawPixels);
        bridge!.Start(token);
        bridge.Update(state.Current);

        if (devices.Count == 0)
        {
            logger!.LogWarning(
                "No classic GOverlay LCDSysInfo display was detected");
        }
        else
        {
            foreach (var device in devices)
                logger!.LogInformation(
                    "Detected {DisplayName} ({Width}x{Height}) at {UsbId}; serial {Serial}; USB hardware revision {UsbRevision}",
                    device.Specification.DisplayName,
                    device.Specification.PixelWidth,
                    device.Specification.PixelHeight,
                    $"{device.Specification.UsbVendorId:X4}:{device.Specification.UsbProductId:X4}",
                    device.SerialNumber ?? "(none)",
                    device.UsbHardwareRevision ?? "unknown");
        }
        logger!.LogInformation(
            "Desktop Shrine - GOverlay DontUseDrawPixels={DontUseDrawPixels}",
            settings.DontUseDrawPixels);

        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bridge is not null)
            await bridge.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        if (bridge is not null)
            await bridge.DisposeAsync();
        if (mediaRoute is not null)
        {
            mediaRoute.Changed -= OnMediaRouteChanged;
            mediaRoute = null;
        }
        foreach (var subscription in subscriptions)
            await subscription.DisposeAsync();
        subscriptions.Clear();
    }

    private ValueTask UpdateMediaAsync(
        MessageEnvelope<NowPlayingState> envelope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
            bridge!.Update(state!.Update(envelope.Payload));
        return ValueTask.CompletedTask;
    }

    private ValueTask UpdatePaletteAsync(
        MessageEnvelope<MediaColourPalette> envelope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
            bridge!.Update(state!.Update(envelope.Payload));
        return ValueTask.CompletedTask;
    }

    private ValueTask UpdateAudioAsync(
        MessageEnvelope<AudioSpectrumFrame> envelope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
            state!.Update(envelope.Payload);
        return ValueTask.CompletedTask;
    }

    private ValueTask UpdateHardwareAsync(
        MessageEnvelope<HardwareMonitorState> envelope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
            bridge!.Update(state!.Update(envelope.Payload));
        return ValueTask.CompletedTask;
    }

    private void OnMediaRouteChanged(
        object? sender,
        ConfigurationChangedEventArgs<InputRouteSnapshot> args)
    {
        _ = sender;
        lock (gate)
            bridge?.Update(state!.UpdateMediaRoute(
                args.Current.SelectedProvider?.PluginId));
    }

    private GOverlayDashboardState PrepareDisplayState(DateTimeOffset now)
    {
        lock (gate)
            return state!.PrepareDisplayState(now);
    }
}
