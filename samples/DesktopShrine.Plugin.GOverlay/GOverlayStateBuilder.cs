using System.Security.Cryptography;
using System.Globalization;
using DesktopShrine.Contracts.Audio;
using DesktopShrine.Contracts.Hardware;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Plugin.GOverlay.Layout;

namespace DesktopShrine.Plugin.GOverlay;

internal sealed class GOverlayStateBuilder
{
    private static long nextRenderGeneration = DateTime.UtcNow.Ticks;
    private static long nextWaterfallResetSequence = DateTime.UtcNow.Ticks;
    private long renderGeneration =
        Interlocked.Increment(ref nextRenderGeneration);
    private string renderRouteKey = "media:startup";
    private readonly GOverlayWaterfallAggregator waterfall;
    private readonly int waterfallColumnWidth;
    private NowPlayingState? media;
    private HardwareMonitorState? hardware;
    private bool mediaRouteActive = true;
    private MediaColourPalette? palette;
    private long revision;
    private long waterfallResetSequence =
        Interlocked.Increment(ref nextWaterfallResetSequence);
    private long waterfallColumnSequence;
    private int waterfallWritePosition;
    private bool audioActive;
    private bool hasWaterfallColumn;
    private float[] waterfallBands = [];
    private string mediaArtworkKey = string.Empty;
    private string? waterfallVisualKey;
    private string fontName = "Oxanium-Bold_20px.bin";
    private bool dontUseDrawPixels = true;

    public GOverlayDashboardState Current { get; private set; } = new();
    public string FontName
    {
        get => fontName;
        set
        {
            fontName = value;
            Rebuild(DateTimeOffset.UtcNow);
        }
    }

    public void ConfigureArtwork(bool value)
    {
        dontUseDrawPixels = value;
        Rebuild(DateTimeOffset.UtcNow);
    }

    public GOverlayStateBuilder(
        GOverlayWaterfallOptions? waterfallOptions = null,
        int waterfallColumnWidth =
            GOverlayWaterfallGeometry.ColumnWidth)
    {
        waterfall = new(
            waterfallOptions ?? new GOverlayWaterfallOptions());
        this.waterfallColumnWidth =
            GOverlayWaterfallGeometry.ValidateColumnWidth(
                waterfallColumnWidth);
        if (waterfall.BandCount
            != GOverlayWaterfallGeometry.BandCount)
            throw new InvalidOperationException(
                "Waterfall aggregation and layout band counts differ.");
        Rebuild(DateTimeOffset.UtcNow);
    }

    public GOverlayDashboardState Update(NowPlayingState value)
    {
        media = value;
        mediaArtworkKey = ArtworkKey(value.Artwork);
        return Rebuild(value.CapturedAt);
    }

    public GOverlayDashboardState Update(HardwareMonitorState value)
    {
        hardware = value;
        return Rebuild(value.CapturedAt);
    }

    public GOverlayDashboardState UpdateMediaRoute(bool isActive)
        => UpdateMediaRoute(isActive ? "media" : null);

    public GOverlayDashboardState UpdateMediaRoute(
        string? selectedProviderId)
    {
        var hasMediaProvider =
            !string.IsNullOrWhiteSpace(selectedProviderId);
        var nextRouteKey = !hasMediaProvider
            ? "hardware"
            : "media:" + selectedProviderId;
        if (!string.Equals(
                renderRouteKey,
                nextRouteKey,
                StringComparison.Ordinal))
        {
            renderRouteKey = nextRouteKey;
            renderGeneration =
                Interlocked.Increment(ref nextRenderGeneration);
        }
        mediaRouteActive = hasMediaProvider;
        return Rebuild(DateTimeOffset.UtcNow);
    }

    public GOverlayDashboardState Update(MediaColourPalette value)
    {
        palette = value;
        return Rebuild(DateTimeOffset.UtcNow);
    }

    public void Update(AudioSpectrumFrame value)
    {
        // Audio is a high-rate stream. Accumulate it here and let the bridge's
        // display tick rebuild once when a waterfall column is ready. Rebuilding
        // for every frame needlessly hashes artwork and can starve progress
        // updates behind the shared state lock.
        waterfall.Add(value);
    }

    public GOverlayDashboardState PrepareDisplayState(DateTimeOffset now)
    {
        var interval = waterfall.Consume(now);
        if (interval.BecameInactive)
        {
            audioActive = false;
            ResetWaterfall(resetAggregator: false);
            return Rebuild(now);
        }
        if (!interval.HasColumn)
        {
            var projected = DisplayPosition(media, now);
            if (projected.TotalSeconds.Equals(Current.PositionSeconds))
                return Current;
            return Rebuild(now);
        }

        audioActive = true;
        hasWaterfallColumn = true;
        waterfallBands = interval.Values;
        waterfallColumnSequence++;
        var columnCount = GOverlayWaterfallGeometry.ColumnCountFor(
            waterfallColumnWidth);
        waterfallWritePosition %= columnCount;
        var result = Rebuild(now);
        waterfallWritePosition =
            (waterfallWritePosition + 1) % columnCount;
        return result;
    }

    private GOverlayDashboardState Rebuild(DateTimeOffset now)
    {
        var hardwareVisible =
            !mediaRouteActive && hardware?.IsAvailable == true;
        var available =
            !hardwareVisible
            && mediaRouteActive
            && media?.IsAvailable == true;
        var artwork = available ? media!.Artwork : null;
        var duration = available ? Duration(media!) : TimeSpan.Zero;
        var position = available
            ? DisplayPosition(media!, now)
            : TimeSpan.Zero;
        var activePalette = palette?.IsAvailable == true ? palette : null;
        var artworkKey = available ? mediaArtworkKey : string.Empty;
        var dominant = Colour(
            activePalette?.OutputDominant.BaseColour,
            new(68, 74, 84));
        var accent = Colour(
            activePalette?.OutputAccent.BaseColour,
            new(83, 196, 255));
        var dark = Colour(
            activePalette?.OutputDark.BaseColour,
            new(14, 17, 22));
        var light = Colour(
            activePalette?.OutputLight.BaseColour,
            new(226, 232, 240));
        ResetWaterfallIfVisualChanged(
            artworkKey,
            activePalette is not null,
            dominant,
            accent,
            dark,
            light);

        Current = new()
        {
            Revision = ++revision,
            RenderGeneration = renderGeneration,
            DontUseDrawPixels = dontUseDrawPixels,
            Mode = hardwareVisible
                ? GOverlayDashboardMode.Hardware
                : GOverlayDashboardMode.Media,
            IsAvailable = available,
            PlaybackStatus = available ? "ACTIVE" : "INACTIVE",
            Title = available
                ? First(media!.Title, media.Subtitle, "Untitled")
                : "Nothing playing",
            Artist = available
                ? First(media!.Artist, media.AlbumArtist, "Unknown artist")
                : string.Empty,
            Context = available
                ? First(
                    media!.Rating?.Summary,
                    media!.AlbumTitle,
                    FriendlySource(media.SourceAppUserModelId),
                    media.Kind == MediaKind.Unknown
                        ? string.Empty
                        : media.Kind.ToString())
                : "Waiting for a media session",
            FontName = fontName,
            PositionSeconds = position.TotalSeconds,
            DurationSeconds = duration.TotalSeconds,
            HasRating = media?.Rating is not null,
            PositiveRatingCount =
                media?.Rating?.PositiveCount ?? 0,
            NegativeRatingCount =
                media?.Rating?.NegativeCount ?? 0,
            RatingSummary = media?.Rating?.Summary,
            Dominant = dominant,
            Accent = accent,
            Dark = dark,
            Light = light,
            HasPalette = activePalette is not null,
            AudioActive = audioActive,
            WaterfallResetSequence = waterfallResetSequence,
            WaterfallColumnSequence = waterfallColumnSequence,
            WaterfallWritePosition = waterfallWritePosition,
            WaterfallColumnWidth = waterfallColumnWidth,
            HasWaterfallColumn = hasWaterfallColumn,
            WaterfallBands = waterfallBands,
            ArtworkKey = artworkKey,
            ArtworkContentType = artwork?.ContentType,
            ArtworkData = artwork?.Data ?? [],
            HardwareProvider = hardware?.Provider ?? string.Empty,
            HardwareProviderVersion =
                hardware?.ProviderVersion ?? string.Empty,
            HardwareCapturedAtUnixMilliseconds =
                hardware?.CapturedAt.ToUnixTimeMilliseconds() ?? 0,
            GpuName = PrimaryGpu(hardware)?.Name ?? "GPU",
            CpuName = First(
                hardware?.System.ProcessorName,
                "CPU"),
            GpuMetrics = HardwareGpuMetrics(hardware),
            CpuMetrics = HardwareCpuMetrics(hardware),
            PhysicalMemorySummary = FormatMemorySummary(
                "RAM",
                hardware?.System.UsedMemoryBytes,
                hardware?.System.TotalMemoryBytes),
            PhysicalMemoryLevel = Level(
                hardware?.System.UsedMemoryBytes,
                hardware?.System.TotalMemoryBytes),
            VirtualMemorySummary = FormatMemorySummary(
                "VIRT",
                hardware?.System.UsedVirtualMemoryBytes,
                hardware?.System.TotalVirtualMemoryBytes),
            VirtualMemoryLevel = Level(
                hardware?.System.UsedVirtualMemoryBytes,
                hardware?.System.TotalVirtualMemoryBytes)
        };
        return Current;
    }

    private static GOverlayHardwareMetricState[] HardwareGpuMetrics(
        HardwareMonitorState? value)
    {
        var gpu = PrimaryGpu(value);
        if (gpu is null)
            return MissingMetrics("LOAD", "VRAM", "HOT", "PWR", "CLK");

        var memoryPercent = Percent(
            gpu.UsedMemoryBytes,
            gpu.TotalMemoryBytes);
        var temperature = gpu.HotspotTemperatureCelsius
            ?? gpu.TemperatureCelsius;
        var power = gpu.TotalBoardPowerWatts ?? gpu.PowerWatts;
        return
        [
            PercentageMetric("LOAD", gpu.UsagePercent),
            PercentageMetric("VRAM", memoryPercent),
            NumberMetric(
                "HOT",
                temperature,
                110,
                number => Format(number, "0") + "°"),
            NumberMetric(
                "PWR",
                power,
                350,
                number => Format(number, "0") + "W"),
            NumberMetric(
                "CLK",
                gpu.ClockMegahertz,
                3_000,
                FormatClock)
        ];
    }

    private static GOverlayHardwareMetricState[] HardwareCpuMetrics(
        HardwareMonitorState? value)
    {
        var system = value?.System;
        return
        [
            PercentageMetric("LOAD", system?.CpuUsagePercent),
            NumberMetric(
                "CMOS",
                system?.CmosBatteryVoltageVolts,
                3.6,
                number => Format(number, "0.00") + "V"),
            NumberMetric(
                "TEMP",
                system?.CpuTemperatureCelsius,
                100,
                number => Format(number, "0") + "°"),
            NumberMetric(
                "PWR",
                system?.CpuPowerWatts,
                system?.CpuPowerLimitWatts is > 0
                    ? system.CpuPowerLimitWatts.Value
                    : 200,
                number => Format(number, "0") + "W"),
            NumberMetric(
                "CLK",
                system?.CpuClockMegahertz,
                system?.CpuClockLimitMegahertz is > 0
                    ? system.CpuClockLimitMegahertz.Value
                    : 6_000,
                FormatClock)
        ];
    }

    private static GraphicsProcessorTelemetry? PrimaryGpu(
        HardwareMonitorState? value) =>
        value?.GraphicsProcessors
            .OrderByDescending(gpu =>
                gpu.Type == GraphicsProcessorType.Discrete)
            .ThenBy(gpu => gpu.Id)
            .FirstOrDefault();

    private static GOverlayHardwareMetricState PercentageMetric(
        string label,
        double? value) =>
        NumberMetric(
            label,
            value,
            100,
            number => Format(number, "0") + "%");

    private static GOverlayHardwareMetricState NumberMetric(
        string label,
        double? value,
        double visualMaximum,
        Func<double, string> format) =>
        value.HasValue && double.IsFinite(value.Value)
            ? new()
            {
                Label = label,
                DisplayValue = format(value.Value),
                Level = Math.Clamp(value.Value / visualMaximum, 0, 1),
                IsAvailable = true
            }
            : MissingMetric(label);

    private static GOverlayHardwareMetricState MissingMetric(
        string label) =>
        new()
        {
            Label = label,
            DisplayValue = "--",
            IsAvailable = false
        };

    private static GOverlayHardwareMetricState[] MissingMetrics(
        params string[] labels) =>
        labels.Select(MissingMetric).ToArray();

    private static double? Percent(long? used, long? total) =>
        used.HasValue && total is > 0
            ? used.Value * 100d / total.Value
            : null;

    private static double Level(long? used, long? total) =>
        used.HasValue && total is > 0
            ? Math.Clamp(used.Value / (double)total.Value, 0, 1)
            : 0;

    private static string Format(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string FormatCompact(double value) =>
        value >= 1_000
            ? Format(value / 1_000, "0.#") + "K"
            : Format(value, "0");

    private static string FormatClock(double megahertz) =>
        megahertz >= 1_000
            ? Format(megahertz / 1_000, "0.#") + "G"
            : Format(megahertz, "0") + "M";

    private static string FormatMemorySummary(
        string label,
        long? usedBytes,
        long? totalBytes)
    {
        if (!usedBytes.HasValue || totalBytes is not > 0)
            return label + "  -- / --";

        const double gibibyte = 1024d * 1024 * 1024;
        return string.Concat(
            label,
            "  ",
            Format(usedBytes.Value / gibibyte, "0.0"),
            " / ",
            Format(totalBytes.Value / gibibyte, "0.0"),
            " GB");
    }

    private void ResetWaterfallIfVisualChanged(
        string artworkKey,
        bool hasPalette,
        GOverlayColour dominant,
        GOverlayColour accent,
        GOverlayColour dark,
        GOverlayColour light)
    {
        var key = string.Join(
            ":",
            artworkKey,
            hasPalette,
            dominant.Hex,
            accent.Hex,
            dark.Hex,
            light.Hex);
        if (waterfallVisualKey is null)
        {
            waterfallVisualKey = key;
            return;
        }
        if (waterfallVisualKey == key)
            return;

        waterfallVisualKey = key;
        ResetWaterfall(resetAggregator: true);
    }

    private void ResetWaterfall(bool resetAggregator)
    {
        if (resetAggregator)
            waterfall.ResetVisualHistory();
        waterfallResetSequence++;
        waterfallWritePosition = 0;
        hasWaterfallColumn = false;
        waterfallBands = [];
    }

    private static TimeSpan Duration(NowPlayingState value)
    {
        var start = value.StartTime ?? value.MinimumSeekTime ?? TimeSpan.Zero;
        var end = value.EndTime ?? value.MaximumSeekTime;
        return end > start ? end.Value - start : TimeSpan.Zero;
    }

    private static TimeSpan Position(NowPlayingState value)
    {
        var start = value.StartTime ?? value.MinimumSeekTime ?? TimeSpan.Zero;
        var position = value.Position ?? start;
        return position > start ? position - start : TimeSpan.Zero;
    }

    private static TimeSpan DisplayPosition(
        NowPlayingState? value,
        DateTimeOffset now)
    {
        if (value?.IsAvailable != true)
            return TimeSpan.Zero;

        var position = Position(value);
        if (value.Status == PlaybackStatus.Playing)
        {
            var anchor =
                value.TimelineLastUpdatedAt ?? value.CapturedAt;
            var elapsed = now - anchor;
            if (elapsed > TimeSpan.Zero)
            {
                var rate = value.PlaybackRate is > 0
                    ? value.PlaybackRate.Value
                    : 1;
                position += TimeSpan.FromTicks(
                    (long)Math.Round(elapsed.Ticks * rate));
            }
        }

        var duration = Duration(value);
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        // The physical progress bar and its labels do not benefit from
        // sub-second churn. One-second projection keeps it live without
        // spending ten rectangle updates per second.
        return TimeSpan.FromSeconds(
            Math.Floor(position.TotalSeconds));
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        ?? string.Empty;

    private static string FriendlySource(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
            return string.Empty;

        var value = appUserModelId;
        var separator = value.LastIndexOfAny(['!', '\\', '/']);
        if (separator >= 0 && separator + 1 < value.Length)
            value = value[(separator + 1)..];
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        return value.Replace('.', ' ');
    }

    private static GOverlayColour Colour(
        BaseColour? value,
        GOverlayColour fallback) =>
        value is { } colour
            ? new(colour.Red, colour.Green, colour.Blue)
            : fallback;

    private static string ArtworkKey(MediaArtwork? artwork) =>
        artwork?.Data is { Length: > 0 } data
            ? Convert.ToHexString(SHA256.HashData(data))
            : string.Empty;
}
