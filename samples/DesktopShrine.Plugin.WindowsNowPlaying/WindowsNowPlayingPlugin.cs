using System.Threading.Channels;
using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Media;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DesktopShrine.Plugin.WindowsNowPlaying;

public sealed class WindowsNowPlayingPlugin : IInputPlugin
{
    private const string PortId = "current-media";
    private readonly Channel<RefreshKind> refreshes = Channel.CreateUnbounded<RefreshKind>(new()
    {
        SingleReader = true,
        SingleWriter = false
    });

    private IPluginContext? context;
    private ILogger<WindowsNowPlayingPlugin>? logger;
    private CancellationTokenSource? stop;
    private Task? worker;
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? session;
    private MediaDetails media = new();
    private WindowsNowPlayingSettings settings = new();
    private ILiveConfiguration<WindowsNowPlayingSettings>? liveSettings;
    private readonly object activityGate = new();
    private CancellationTokenSource? pausedTimeout;
    private bool pauseExpired;
    private PlaybackStatus? lastObservedPlaybackStatus;
    private InputActivityState activityState =
        InputActivityState.Inactive;

    public WindowsNowPlayingPlugin() { }

    internal WindowsNowPlayingPlugin(
        WindowsNowPlayingSettings initialSettings)
    {
        settings = initialSettings;
    }

    public PluginDescriptor Descriptor { get; } = new()
    {
        Id = "windows-now-playing",
        Name = "Windows Now Playing",
        Version = new(1, 0, 0),
        Description = "Publishes the active Windows system media session.",
        SupportedPlatforms = ["windows"]
    };

    public IReadOnlyCollection<ProvidedPortDescriptor> ProvidedPorts { get; } =
    [
        new()
        {
            PortId = PortId,
            DisplayName = "Current media",
            Contract = new("desktop-shrine.media.now-playing", new(1, 2, 0))
        }
    ];

    public InputActivityState ActivityState
    {
        get
        {
            lock (activityGate)
                return activityState;
        }
    }

    public event EventHandler<InputActivityStateChangedEventArgs>?
        ActivityStateChanged;

    public ValueTask InitialiseAsync(IPluginContext value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        context = value;
        logger = value.LoggerFactory.CreateLogger<WindowsNowPlayingPlugin>();
        liveSettings = value.ObserveConfiguration(
            WindowsNowPlayingSettings.FromConfiguration);
        settings = liveSettings.Current;
        liveSettings.Changed += OnSettingsChanged;
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Windows Now Playing requires Windows 10 version 1809 or newer.");

        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        manager.CurrentSessionChanged += OnCurrentSessionChanged;
        manager.SessionsChanged += OnSessionsChanged;
        BindSession(manager.GetCurrentSession());

        worker = RunAsync(stop.Token);
        refreshes.Writer.TryWrite(RefreshKind.All);
        logger!.LogInformation("Windows Now Playing input started");
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        UnbindManager();
        BindSession(null);
        refreshes.Writer.TryComplete();
        ResetActivity(
            InputActivityState.Inactive,
            forgetPlayback: true);

        if (stop is null)
            return;

        await stop.CancelAsync();
        if (worker is not null)
        {
            try { await worker.WaitAsync(token); }
            catch (OperationCanceledException) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (liveSettings is not null)
            liveSettings.Changed -= OnSettingsChanged;
        UnbindManager();
        BindSession(null);
        ResetActivity(
            InputActivityState.Inactive,
            forgetPlayback: true);
        stop?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnSettingsChanged(
        object? sender,
        ConfigurationChangedEventArgs<WindowsNowPlayingSettings> args)
    {
        // Artwork and timeline options are read for every refresh. The pause
        // timer retains its current duration until the next state transition.
        settings = args.Current;
        refreshes.Writer.TryWrite(RefreshKind.All);
        logger!.LogInformation("Windows media settings updated live");
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (var kind in refreshes.Reader.ReadAllAsync(token))
            {
                if ((kind & RefreshKind.Session) != 0)
                    BindSession(manager?.GetCurrentSession());

                if ((kind & RefreshKind.Media) != 0)
                    media = await ReadMediaAsync(session);

                await PublishAsync(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger!.LogError(exception, "Windows Now Playing input failed");
            throw;
        }
    }

    private async Task PublishAsync(CancellationToken token)
    {
        var current = session;
        if (current is null)
        {
            ResetActivity(
                InputActivityState.Inactive,
                forgetPlayback: true);
            await context!.Publisher.PublishAsync(PortId, new NowPlayingState
            {
                IsAvailable = false,
                CapturedAt = DateTimeOffset.UtcNow
            }, token);
            logger!.LogDebug("Published empty now-playing state; there is no active media session");
            return;
        }

        var playback = current.GetPlaybackInfo();
        var timeline = current.GetTimelineProperties();
        var controls = playback?.Controls;
        var status = Map(playback?.PlaybackStatus);
        UpdateActivity(status);
        var snapshot = new NowPlayingState
        {
            IsAvailable = true,
            CapturedAt = DateTimeOffset.UtcNow,
            SourceAppUserModelId = current.SourceAppUserModelId,
            Title = media.Title,
            Subtitle = media.Subtitle,
            Artist = media.Artist,
            AlbumArtist = media.AlbumArtist,
            AlbumTitle = media.AlbumTitle,
            TrackNumber = media.TrackNumber,
            AlbumTrackCount = media.AlbumTrackCount,
            Genres = media.Genres,
            Kind = media.Kind,
            Artwork = media.Artwork,
            Status = status,
            PlaybackRate = playback?.PlaybackRate,
            IsShuffleActive = playback?.IsShuffleActive,
            Repeat = Map(playback?.AutoRepeatMode),
            Controls = controls is null ? new() : new()
            {
                CanChannelDown = controls.IsChannelDownEnabled,
                CanChannelUp = controls.IsChannelUpEnabled,
                CanFastForward = controls.IsFastForwardEnabled,
                CanGoNext = controls.IsNextEnabled,
                CanPause = controls.IsPauseEnabled,
                CanPlay = controls.IsPlayEnabled,
                CanChangePosition = controls.IsPlaybackPositionEnabled,
                CanChangePlaybackRate = controls.IsPlaybackRateEnabled,
                CanGoPrevious = controls.IsPreviousEnabled,
                CanRecord = controls.IsRecordEnabled,
                CanChangeRepeat = controls.IsRepeatEnabled,
                CanRewind = controls.IsRewindEnabled,
                CanChangeShuffle = controls.IsShuffleEnabled,
                CanStop = controls.IsStopEnabled
            },
            Position = timeline?.Position,
            StartTime = timeline?.StartTime,
            EndTime = timeline?.EndTime,
            MinimumSeekTime = timeline?.MinSeekTime,
            MaximumSeekTime = timeline?.MaxSeekTime,
            TimelineLastUpdatedAt = timeline?.LastUpdatedTime
        };

        await context!.Publisher.PublishAsync(PortId, snapshot, token);
        logger!.LogDebug(
            "Published now playing: {Title} by {Artist} from {SourceApp} ({Status})",
            snapshot.Title,
            snapshot.Artist,
            snapshot.SourceAppUserModelId,
            snapshot.Status);
    }

    private async Task<MediaDetails> ReadMediaAsync(GlobalSystemMediaTransportControlsSession? current)
    {
        if (current is null)
            return new();

        try
        {
            var properties = await current.TryGetMediaPropertiesAsync();
            return new()
            {
                Title = EmptyToNull(properties.Title),
                Subtitle = EmptyToNull(properties.Subtitle),
                Artist = EmptyToNull(properties.Artist),
                AlbumArtist = EmptyToNull(properties.AlbumArtist),
                AlbumTitle = EmptyToNull(properties.AlbumTitle),
                TrackNumber = properties.TrackNumber == 0 ? null : properties.TrackNumber,
                AlbumTrackCount = properties.AlbumTrackCount == 0 ? null : properties.AlbumTrackCount,
                Genres = [.. properties.Genres],
                Kind = Map(properties.PlaybackType),
                Artwork = settings.IncludeArtwork
                    ? await ReadArtworkAsync(properties.Thumbnail)
                    : null
            };
        }
        catch (Exception exception)
        {
            logger!.LogWarning(exception, "Could not read media properties from {SourceApp}", current.SourceAppUserModelId);
            return new();
        }
    }

    private async Task<MediaArtwork?> ReadArtworkAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
            return null;

        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size > settings.MaximumArtworkBytes)
            {
                logger!.LogWarning(
                    "Ignored media artwork larger than {MaximumBytes} bytes",
                    settings.MaximumArtworkBytes);
                return null;
            }

            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var byteCount = await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[byteCount];
            reader.ReadBytes(bytes);
            return new() { ContentType = EmptyToNull(stream.ContentType), Data = bytes };
        }
        catch (Exception exception)
        {
            logger!.LogWarning(exception, "Could not read media artwork");
            return null;
        }
    }

    private void BindSession(GlobalSystemMediaTransportControlsSession? value)
    {
        if (ReferenceEquals(session, value))
            return;

        if (session is not null)
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        session = value;
        media = new();
        ResetActivity(
            InputActivityState.Inactive,
            forgetPlayback: true);

        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
    }

    private void UnbindManager()
    {
        if (manager is null)
            return;

        manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        manager.SessionsChanged -= OnSessionsChanged;
        manager = null;
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        refreshes.Writer.TryWrite(RefreshKind.All);

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) =>
        refreshes.Writer.TryWrite(RefreshKind.All);

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        refreshes.Writer.TryWrite(RefreshKind.Media);

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        refreshes.Writer.TryWrite(RefreshKind.Playback);

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        if (settings.PublishTimelineUpdates)
            refreshes.Writer.TryWrite(RefreshKind.Timeline);
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static PlaybackStatus Map(GlobalSystemMediaTransportControlsSessionPlaybackStatus? value) => value switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackStatus.Closed,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => PlaybackStatus.Opened,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PlaybackStatus.Changing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackStatus.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackStatus.Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackStatus.Playing,
        _ => PlaybackStatus.Unknown
    };

    private static MediaKind Map(Windows.Media.MediaPlaybackType? value) => value switch
    {
        Windows.Media.MediaPlaybackType.Music => MediaKind.Music,
        Windows.Media.MediaPlaybackType.Video => MediaKind.Video,
        Windows.Media.MediaPlaybackType.Image => MediaKind.Image,
        _ => MediaKind.Unknown
    };

    private static RepeatMode Map(Windows.Media.MediaPlaybackAutoRepeatMode? value) => value switch
    {
        Windows.Media.MediaPlaybackAutoRepeatMode.None => RepeatMode.None,
        Windows.Media.MediaPlaybackAutoRepeatMode.Track => RepeatMode.Track,
        Windows.Media.MediaPlaybackAutoRepeatMode.List => RepeatMode.List,
        _ => RepeatMode.Unknown
    };

    internal void UpdateActivity(PlaybackStatus status)
    {
        PlaybackStatus? previousStatus;
        bool pauseAlreadyStarted;
        lock (activityGate)
        {
            previousStatus = lastObservedPlaybackStatus;
            lastObservedPlaybackStatus = status;
            pauseAlreadyStarted = pausedTimeout is not null;
        }

        if (status == PlaybackStatus.Paused)
        {
            // GSMTC does not tell us when a session that was already paused
            // before startup entered that state. Do not claim the media route
            // for a fresh timeout in that case. The grace period is reserved
            // for a playing-to-paused transition observed during this run.
            if (pauseAlreadyStarted
                || previousStatus is PlaybackStatus.Playing
                    or PlaybackStatus.Changing)
                BeginOrContinuePause();
            else
                ResetActivity(InputActivityState.Inactive);
            return;
        }

        ResetActivity(
            status is PlaybackStatus.Playing or PlaybackStatus.Changing
                ? InputActivityState.Active
                : InputActivityState.Inactive);
    }

    private void BeginOrContinuePause()
    {
        CancellationTokenSource? timer = null;
        InputActivityStateChangedEventArgs? changed;
        lock (activityGate)
        {
            if (settings.PausedTimeout == TimeSpan.Zero)
            {
                changed = ChangeActivityLocked(InputActivityState.Inactive);
            }
            else
            {
                if (pausedTimeout is null)
                {
                    pausedTimeout = stop is null
                        ? new()
                        : CancellationTokenSource.CreateLinkedTokenSource(
                            stop.Token);
                    pauseExpired = false;
                    timer = pausedTimeout;
                }

                changed = ChangeActivityLocked(
                    pauseExpired
                        ? InputActivityState.Inactive
                        : InputActivityState.Active);
            }
        }

        RaiseActivityChanged(changed);
        if (timer is not null)
            _ = ExpirePauseAsync(timer);
    }

    private async Task ExpirePauseAsync(CancellationTokenSource timer)
    {
        try
        {
            await Task.Delay(settings.PausedTimeout, timer.Token);
        }
        catch (OperationCanceledException) when (
            timer.IsCancellationRequested)
        {
            return;
        }

        InputActivityStateChangedEventArgs? changed = null;
        lock (activityGate)
        {
            if (ReferenceEquals(pausedTimeout, timer))
            {
                pauseExpired = true;
                changed = ChangeActivityLocked(
                    InputActivityState.Inactive);
            }
        }
        RaiseActivityChanged(changed);
    }

    private void ResetActivity(
        InputActivityState next,
        bool forgetPlayback = false)
    {
        CancellationTokenSource? timer;
        InputActivityStateChangedEventArgs? changed;
        lock (activityGate)
        {
            if (forgetPlayback)
                lastObservedPlaybackStatus = null;
            timer = pausedTimeout;
            pausedTimeout = null;
            pauseExpired = false;
            changed = ChangeActivityLocked(next);
        }

        if (timer is not null)
        {
            timer.Cancel();
            timer.Dispose();
        }
        RaiseActivityChanged(changed);
    }

    private InputActivityStateChangedEventArgs? ChangeActivityLocked(
        InputActivityState next)
    {
        var previous = activityState;
        if (previous == next)
            return null;
        activityState = next;
        return new(previous, next);
    }

    private void RaiseActivityChanged(
        InputActivityStateChangedEventArgs? changed)
    {
        if (changed is not null)
            ActivityStateChanged?.Invoke(this, changed);
    }

    [Flags]
    private enum RefreshKind { Playback = 1, Timeline = 2, Media = 4, Session = 8, All = Playback | Timeline | Media | Session }

    private sealed record MediaDetails
    {
        public string? Title { get; init; }
        public string? Subtitle { get; init; }
        public string? Artist { get; init; }
        public string? AlbumArtist { get; init; }
        public string? AlbumTitle { get; init; }
        public int? TrackNumber { get; init; }
        public int? AlbumTrackCount { get; init; }
        public IReadOnlyList<string> Genres { get; init; } = [];
        public MediaKind Kind { get; init; }
        public MediaArtwork? Artwork { get; init; }
    }
}
