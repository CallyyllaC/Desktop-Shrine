using System.Security.Cryptography;
using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Media;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Plugin.ArtworkPalette;

public sealed class ArtworkPalettePlugin : IInputPlugin, IOutputPlugin
{
    private const string InputPortId = "now-playing";
    private const string OutputPortId = "colour-palette";
    private IPluginContext? context;
    private ILogger<ArtworkPalettePlugin>? logger;
    private IAsyncDisposable? subscription;
    private ArtworkPaletteSettings settings = new();
    private byte[]? lastArtworkHash;
    private bool hasPublishedPaletteState;
    private bool paletteIsAvailable;

    public PluginDescriptor Descriptor { get; } = new()
    {
        Id = "artwork-palette",
        Name = "Artwork Colour Palette",
        Version = new(1, 0, 0),
        Description = "Extracts a representative colour palette from Now Playing artwork.",
        SupportedPlatforms = ["windows"]
    };

    public IReadOnlyCollection<RequiredPortDescriptor> RequiredPorts { get; } =
    [
        new()
        {
            PortId = InputPortId,
            DisplayName = "Now Playing artwork",
            Requirement = new(
                "desktop-shrine.media.now-playing",
                VersionRange.Between(new(1, 1, 0), new(2, 0, 0)))
        }
    ];

    public IReadOnlyCollection<ProvidedPortDescriptor> ProvidedPorts { get; } =
    [
        new()
        {
            PortId = OutputPortId,
            DisplayName = "Media colour palette",
            Contract = new("desktop-shrine.media.colour-palette", new(1, 0, 0))
        }
    ];

    public ValueTask InitialiseAsync(IPluginContext value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        context = value;
        logger = value.LoggerFactory.CreateLogger<ArtworkPalettePlugin>();
        settings = ArtworkPaletteSettings.FromConfiguration(value.Configuration);
        subscription = value.Subscriber.Subscribe<NowPlayingState>(InputPortId, ProcessArtworkAsync);
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (subscription is not null)
            await subscription.DisposeAsync();
    }

    private async ValueTask ProcessArtworkAsync(MessageEnvelope<NowPlayingState> envelope, CancellationToken token)
    {
        var artwork = envelope.Payload.Artwork;
        if (artwork?.Data is not { Length: > 0 })
        {
            lastArtworkHash = null;
            await PublishUnavailableAsync(token);
            return;
        }

        var artworkHash = SHA256.HashData(artwork.Data);
        if (lastArtworkHash is not null && artworkHash.AsSpan().SequenceEqual(lastArtworkHash))
            return;

        try
        {
            var decoded = await ArtworkDecoder.DecodeAsync(artwork.Data, token);
            var palette = ArtworkPaletteExtractor.Extract(
                decoded.Pixels,
                decoded.Width,
                decoded.Height,
                decoded.IsPremultiplied,
                settings);
            if (!palette.IsAvailable)
            {
                lastArtworkHash = artworkHash;
                await PublishUnavailableAsync(token);
                return;
            }

            await context!.Publisher.PublishAsync(OutputPortId, palette, token);
            lastArtworkHash = artworkHash;
            hasPublishedPaletteState = true;
            paletteIsAvailable = true;

            logger!.LogInformation(
                "Artwork palette for {Title}: dominant {Dominant}, accent {Accent}, dark {Dark}, light {Light}",
                envelope.Payload.Title ?? "(untitled)",
                palette.Dominant.BaseColour.Hex,
                palette.Accent.BaseColour.Hex,
                palette.Dark.BaseColour.Hex,
                palette.Light.BaseColour.Hex);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger!.LogWarning(exception, "Could not extract an artwork palette for {Title}", envelope.Payload.Title ?? "(untitled)");
            lastArtworkHash = null;
            await PublishUnavailableAsync(token);
        }
    }

    private async ValueTask PublishUnavailableAsync(CancellationToken token)
    {
        if (hasPublishedPaletteState && !paletteIsAvailable)
            return;

        await context!.Publisher.PublishAsync(OutputPortId, MediaColourPalette.Unavailable, token);
        hasPublishedPaletteState = true;
        paletteIsAvailable = false;
        logger!.LogInformation("No usable artwork palette is available");
    }
}
