using DesktopShrine.Abstractions;
[assembly: ShrineContractPackage("desktop-shrine.contracts.media", "1.3.0")]
namespace DesktopShrine.Contracts.Media;

public enum PlaybackStatus { Unknown, Closed, Opened, Changing, Stopped, Paused, Playing }
public enum MediaKind { Unknown, Music, Video, Image }
public enum RepeatMode { Unknown, None, Track, List }

public sealed record MediaArtwork
{
    public string? ContentType { get; init; }
    public required byte[] Data { get; init; }
}

public sealed record PlaybackCapabilities
{
    public bool CanChannelDown { get; init; }
    public bool CanChannelUp { get; init; }
    public bool CanFastForward { get; init; }
    public bool CanGoNext { get; init; }
    public bool CanPause { get; init; }
    public bool CanPlay { get; init; }
    public bool CanChangePosition { get; init; }
    public bool CanChangePlaybackRate { get; init; }
    public bool CanGoPrevious { get; init; }
    public bool CanRecord { get; init; }
    public bool CanChangeRepeat { get; init; }
    public bool CanRewind { get; init; }
    public bool CanChangeShuffle { get; init; }
    public bool CanStop { get; init; }
}

public sealed record AudienceRating
{
    public required long PositiveCount { get; init; }
    public required long NegativeCount { get; init; }
    public string? Summary { get; init; }
}

[ShrineContract("desktop-shrine.media.now-playing", "1.2.0", DeliveryKind.State)]
public sealed record NowPlayingState : IShrineState
{
    public required bool IsAvailable { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public string? SourceAppUserModelId { get; init; }
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
    public PlaybackStatus Status { get; init; }
    public double? PlaybackRate { get; init; }
    public bool? IsShuffleActive { get; init; }
    public RepeatMode Repeat { get; init; }
    public AudienceRating? Rating { get; init; }
    public PlaybackCapabilities Controls { get; init; } = new();
    public TimeSpan? Position { get; init; }
    public TimeSpan? StartTime { get; init; }
    public TimeSpan? EndTime { get; init; }
    public TimeSpan? MinimumSeekTime { get; init; }
    public TimeSpan? MaximumSeekTime { get; init; }
    public DateTimeOffset? TimelineLastUpdatedAt { get; init; }
}
