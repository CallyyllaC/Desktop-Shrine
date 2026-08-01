using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Media;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Plugin.SteamNowPlaying;

public sealed class SteamNowPlayingPlugin : IInputPlugin
{
    private const string PortId = "current-game";
    private readonly ISteamRuntime steam;
    private IPluginContext? context;
    private ILogger<SteamNowPlayingPlugin>? logger;
    private SteamNowPlayingSettings? settings;
    private HttpClient? httpClient;
    private SteamArtworkProvider? artworkProvider;
    private SteamStoreMetadataProvider? storeMetadataProvider;
    private CancellationTokenSource? stop;
    private Task? worker;
    private uint? lastAppId;
    private bool hasObservedAppId;
    private bool lastGameWasRecognised;
    private InputActivityState activityState =
        InputActivityState.Inactive;

    public SteamNowPlayingPlugin() : this(new WindowsSteamRuntime()) { }

    internal SteamNowPlayingPlugin(ISteamRuntime steam)
    {
        this.steam = steam;
    }

    public PluginDescriptor Descriptor { get; } = new()
    {
        Id = "steam-now-playing",
        Name = "Steam Now Playing",
        Version = new(1, 0, 0),
        Description =
            "Publishes the game currently reported by the Windows Steam client.",
        SupportedPlatforms = ["windows"]
    };

    public IReadOnlyCollection<ProvidedPortDescriptor> ProvidedPorts { get; } =
    [
        new()
        {
            PortId = PortId,
            DisplayName = "Current Steam game",
            Contract = new(
                "desktop-shrine.media.now-playing",
                new(1, 2, 0))
        }
    ];

    public DefaultInputPriorityPlacement DefaultPriorityPlacement =>
        DefaultInputPriorityPlacement.Highest;

    public InputActivityState ActivityState => activityState;

    public event EventHandler<InputActivityStateChangedEventArgs>?
        ActivityStateChanged;

    public ValueTask InitialiseAsync(
        IPluginContext value,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        context = value;
        logger = value.LoggerFactory.CreateLogger<SteamNowPlayingPlugin>();
        settings = SteamNowPlayingSettings.FromConfiguration(
            value.Configuration);
        httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(
                settings.ArtworkDownloadTimeoutSeconds)
        };
        artworkProvider = new(httpClient, logger, settings);
        storeMetadataProvider = new(httpClient, logger, settings);
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Steam Now Playing requires Windows.");
        if (context is null || settings is null)
            throw new InvalidOperationException(
                "Steam Now Playing has not been initialised.");

        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        worker = RunAsync(stop.Token);
        logger!.LogInformation(
            "Steam Now Playing input started; polling every {PollIntervalMilliseconds} ms",
            settings.PollIntervalMilliseconds);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        if (stop is null)
            return;

        await stop.CancelAsync();
        if (worker is not null)
        {
            try { await worker.WaitAsync(token); }
            catch (OperationCanceledException) { }
        }
        SetActivity(InputActivityState.Inactive);
    }

    public ValueTask DisposeAsync()
    {
        stop?.Dispose();
        httpClient?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(token);
                }
                catch (Exception exception) when (
                    !token.IsCancellationRequested)
                {
                    logger!.LogWarning(
                        exception,
                        "Could not refresh the current Steam game");
                }

                await Task.Delay(
                    settings!.PollIntervalMilliseconds,
                    token);
            }
        }
        catch (OperationCanceledException) when (
            token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger!.LogError(
                exception,
                "Steam Now Playing input failed");
            throw;
        }
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        var appId = steam.GetRunningAppId();
        var sameApp = hasObservedAppId && appId == lastAppId;
        if (sameApp && (appId is null || lastGameWasRecognised))
            return;

        if (appId is null)
        {
            await context!.Publisher.PublishAsync(
                PortId,
                new NowPlayingState
                {
                    IsAvailable = false,
                    CapturedAt = DateTimeOffset.UtcNow,
                    SourceAppUserModelId = "steam"
                },
                token);
            lastAppId = null;
            hasObservedAppId = true;
            lastGameWasRecognised = false;
            SetActivity(InputActivityState.Inactive);
            logger!.LogDebug(
                "Published empty Steam state; no Steam game is running");
            return;
        }

        var steamDirectory = steam.GetSteamDirectory();
        SteamGame? game = null;
        try
        {
            if (steamDirectory is not null)
            {
                game = SteamLibraryCatalog.FindGame(
                    steamDirectory,
                    appId.Value);
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or FormatException
            or ArgumentException
            or NotSupportedException)
        {
            logger!.LogWarning(
                exception,
                "Could not resolve Steam manifest for AppID {AppId}; publishing the fallback title",
                appId);
        }

        if (sameApp && game is null)
            return;

        var snapshot = CreateSnapshot(
            appId.Value,
            game,
            null,
            null);
        SetActivity(InputActivityState.Active);
        await context!.Publisher.PublishAsync(PortId, snapshot, token);
        lastAppId = appId;
        hasObservedAppId = true;
        lastGameWasRecognised = game is not null;
        logger!.LogInformation(
            "Published Steam game {Title} (AppID {AppId})",
            snapshot.Title,
            appId);

        if (!settings!.IncludeArtwork
            && !settings.IncludeStoreMetadata)
            return;

        var metadataTask = settings.IncludeStoreMetadata
            ? LoadStoreMetadataAsync(appId.Value, token)
            : Task.FromResult<SteamStoreMetadata?>(null);
        var artworkTask = settings.IncludeArtwork
            ? LoadArtworkAsync(
                appId.Value,
                steamDirectory,
                game,
                token)
            : Task.FromResult<MediaArtwork?>(null);
        await Task.WhenAll(metadataTask, artworkTask);
        var metadata = await metadataTask;
        var artwork = await artworkTask;
        if ((metadata is null && artwork is null)
            || steam.GetRunningAppId() != appId)
            return;

        await context.Publisher.PublishAsync(
            PortId,
            CreateSnapshot(
                appId.Value,
                game,
                artwork,
                metadata),
            token);
    }

    private async Task<SteamStoreMetadata?> LoadStoreMetadataAsync(
        uint appId,
        CancellationToken token)
    {
        try
        {
            return await storeMetadataProvider!.GetAsync(appId, token);
        }
        catch (OperationCanceledException) when (
            token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger!.LogWarning(
                exception,
                "Could not load Steam store metadata for AppID {AppId}",
                appId);
            return null;
        }
    }

    private async Task<MediaArtwork?> LoadArtworkAsync(
        uint appId,
        string? steamDirectory,
        SteamGame? game,
        CancellationToken token)
    {
        try
        {
            return await artworkProvider!.GetAsync(
                appId,
                steamDirectory ?? string.Empty,
                game?.InstallDirectory,
                token);
        }
        catch (OperationCanceledException) when (
            token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger!.LogWarning(
                exception,
                "Could not load artwork for Steam AppID {AppId}",
                appId);
            return null;
        }
    }

    private NowPlayingState CreateSnapshot(
        uint appId,
        SteamGame? game,
        MediaArtwork? artwork,
        SteamStoreMetadata? metadata) =>
        new()
        {
            IsAvailable = true,
            CapturedAt = DateTimeOffset.UtcNow,
            SourceAppUserModelId = $"steam:{appId}",
            Title = game?.Name ?? settings!.UnknownGameTitle,
            Subtitle = game?.InstallDirectory ?? $"Steam App {appId}",
            Artist = settings!.IncludeStoreMetadata
                ? metadata?.Publisher ?? "Unknown publisher"
                : null,
            AlbumTitle = "Steam",
            Kind = MediaKind.Unknown,
            Artwork = artwork,
            Rating = metadata?.Rating,
            Status = PlaybackStatus.Playing
        };

    private void SetActivity(InputActivityState next)
    {
        var previous = activityState;
        if (previous == next)
            return;

        activityState = next;
        ActivityStateChanged?.Invoke(this, new(previous, next));
    }
}
