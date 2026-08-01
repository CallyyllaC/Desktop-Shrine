using System.Globalization;

namespace DesktopShrine.Plugin.GOverlay.Layout;

public interface IGOverlayHeaderRegion
{
    GOverlayRegionScene Compose(
        GOverlayDashboardState state,
        GOverlayRectangle bounds,
        DateTime localTime);
}

public interface IGOverlayContentRegion
{
    GOverlayRegionScene Compose(
        GOverlayDashboardState state,
        GOverlayRectangle bounds,
        DateTime localTime);
}

public interface IGOverlayFooterRegion
{
    GOverlayRegionScene Compose(
        GOverlayDashboardState state,
        GOverlayRectangle bounds,
        DateTime localTime);
}

public sealed class GOverlayDashboardLayout
{
    public static readonly GOverlayRectangle HeaderBounds = new(0, 0, 480, 28);
    public static readonly GOverlayRectangle ContentBounds = new(0, 28, 480, 220);
    public static readonly GOverlayRectangle FooterBounds = new(0, 248, 480, 72);

    private readonly IGOverlayHeaderRegion header;
    private readonly IGOverlayContentRegion content;
    private readonly IGOverlayFooterRegion footer;

    public GOverlayDashboardLayout(
        IGOverlayHeaderRegion? header = null,
        IGOverlayContentRegion? content = null,
        IGOverlayFooterRegion? footer = null)
    {
        this.header = header ?? new MediaSessionHeaderRegion();
        this.content = content ?? new NowPlayingContentRegion();
        this.footer = footer ?? new PlaybackContextFooterRegion();
    }

    public GOverlayDashboardScene Compose(
        GOverlayDashboardState state,
        DateTime localTime)
    {
        if (state.Mode == GOverlayDashboardMode.Hardware)
        {
            return new(
                new HardwareMonitorHeaderRegion().Compose(
                    state,
                    HeaderBounds,
                    localTime),
                new HardwareMonitorContentRegion().Compose(
                    state,
                    ContentBounds,
                    localTime),
                new HardwareMonitorFooterRegion().Compose(
                    state,
                    FooterBounds,
                    localTime));
        }

        return new(
            header.Compose(state, HeaderBounds, localTime),
            content.Compose(state, ContentBounds, localTime),
            footer.Compose(state, FooterBounds, localTime));
    }
}

public sealed class HardwareMonitorHeaderRegion : IGOverlayHeaderRegion
{
    public GOverlayRegionScene Compose(
        GOverlayDashboardState state,
        GOverlayRectangle bounds,
        DateTime localTime)
    {
        _ = state;
        _ = localTime;
        return new(
            "header",
            bounds,
            [
                new GOverlayFillRectangleCommand(
                    "hardware.header.background",
                    bounds,
                    GOverlayTheme.Background),
                new GOverlayTextCommand(
                    "hardware.header.label",
                    new(9, 4, 462, 20),
                    "HARDWARE MONITOR",
                    15,
                    GOverlayTheme.HardwareTitle,
                    GOverlayTheme.Background,
                    GOverlayTextAlignment.Centre),
                new GOverlayLineCommand(
                    "hardware.header.separator",
                    0,
                    bounds.Bottom - 1,
                    bounds.Right - 1,
                    bounds.Bottom - 1,
                    GOverlayTheme.Divider)
            ]);
    }
}

public sealed class HardwareMonitorContentRegion : IGOverlayContentRegion
{
    private const int MaximumTitleCharacters = 27;
    private const int MetricCount = 5;

    public GOverlayRegionScene Compose(
        GOverlayDashboardState state,
        GOverlayRectangle bounds,
        DateTime localTime)
    {
        _ = localTime;
        var commands = new List<GOverlayDrawCommand>
        {
            new GOverlayFillRectangleCommand(
                "hardware.content.background",
                bounds,
                GOverlayTheme.Background),
            new GOverlayLineCommand(
                "hardware.content.centre",
                240,
                36,
                240,
                bounds.Bottom - 8,
                GOverlayTheme.Divider),
            new GOverlayTextCommand(
                "hardware.gpu.name",
                new(10, 36, 218, 21),
                Truncate(state.GpuName, MaximumTitleCharacters),
                14,
                GOverlayTheme.Gpu,
                GOverlayTheme.Background,
                GOverlayTextAlignment.Right),
            new GOverlayTextCommand(
                "hardware.cpu.name",
                new(252, 36, 218, 21),
                Truncate(state.CpuName, MaximumTitleCharacters),
                14,
                GOverlayTheme.Cpu,
                GOverlayTheme.Background)
        };

        AddMetrics(
            commands,
            "gpu",
            state.GpuMetrics,
            x: 10,
            GOverlayTextAlignment.Right,
            GOverlayProgressDirection.RightToLeft,
            GOverlayTheme.Gpu);
        AddMetrics(
            commands,
            "cpu",
            state.CpuMetrics,
            x: 252,
            GOverlayTextAlignment.Left,
            GOverlayProgressDirection.LeftToRight,
            GOverlayTheme.Cpu);
        return new("content", bounds, commands);
    }

    private static void AddMetrics(
        ICollection<GOverlayDrawCommand> commands,
        string side,
        IReadOnlyList<GOverlayHardwareMetricState> metrics,
        int x,
        GOverlayTextAlignment alignment,
        GOverlayProgressDirection direction,
        GOverlayColour colour)
    {
        for (var index = 0; index < MetricCount; index++)
        {
            var metric = index < metrics.Count
                ? metrics[index]
                : new GOverlayHardwareMetricState();
            var y = 64 + (index * 34);
            var key = "hardware." + side + "." + index;
            var textColour = metric.IsAvailable
                ? GOverlayTheme.HardwareText
                : GOverlayTheme.Inactive;
            var text = string.IsNullOrWhiteSpace(metric.Label)
                ? "--"
                : metric.Label + "  " + metric.DisplayValue;
            commands.Add(new GOverlayTextCommand(
                key + ".text",
                new(x, y, 218, 19),
                text,
                13,
                textColour,
                GOverlayTheme.Background,
                alignment));
            commands.Add(new GOverlayProgressCommand(
                key + ".meter",
                new(x, y + 23, 218, 6),
                metric.IsAvailable ? metric.Level : 0,
                colour,
                GOverlayTheme.MeterBackground,
                direction));
        }
    }

    private static string Truncate(string value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        if (value.Length <= maximumCharacters)
            return value;
        return value.Substring(0, maximumCharacters - 1) + "…";
    }
}

public sealed class HardwareMonitorFooterRegion : IGOverlayFooterRegion
{
    public GOverlayRegionScene Compose(
        GOverlayDashboardState state,
        GOverlayRectangle bounds,
        DateTime localTime)
    {
        _ = localTime;
        return new(
            "footer",
            bounds,
            [
                new GOverlayFillRectangleCommand(
                    "hardware.footer.background",
                    bounds,
                    GOverlayTheme.Background),
                new GOverlayLineCommand(
                    "hardware.footer.separator",
                    0,
                    bounds.Y,
                    bounds.Right - 1,
                    bounds.Y,
                    GOverlayTheme.Divider),
                new GOverlayLineCommand(
                    "hardware.footer.centre",
                    240,
                    bounds.Y + 9,
                    240,
                    bounds.Bottom - 9,
                    GOverlayTheme.Divider),
                new GOverlayTextCommand(
                    "hardware.footer.physical-memory",
                    new(10, 265, 218, 20),
                    state.PhysicalMemorySummary,
                    12,
                    GOverlayTheme.Muted,
                    GOverlayTheme.Background,
                    GOverlayTextAlignment.Right),
                new GOverlayProgressCommand(
                    "hardware.footer.physical-memory.meter",
                    new(10, 294, 218, 6),
                    state.PhysicalMemoryLevel,
                    GOverlayTheme.Gpu,
                    GOverlayTheme.MeterBackground,
                    GOverlayProgressDirection.RightToLeft),
                new GOverlayTextCommand(
                    "hardware.footer.virtual-memory",
                    new(252, 265, 218, 20),
                    state.VirtualMemorySummary,
                    12,
                    GOverlayTheme.Muted,
                    GOverlayTheme.Background),
                new GOverlayProgressCommand(
                    "hardware.footer.virtual-memory.meter",
                    new(252, 294, 218, 6),
                    state.VirtualMemoryLevel,
                    GOverlayTheme.Cpu,
                    GOverlayTheme.MeterBackground,
                    GOverlayProgressDirection.LeftToRight)
            ]);
    }
}

public sealed class MediaSessionHeaderRegion : IGOverlayHeaderRegion
{
    public GOverlayRegionScene Compose(
        GOverlayDashboardState state,
        GOverlayRectangle bounds,
        DateTime localTime)
    {
        var background = GOverlayTheme.Background;
        var commands = new List<GOverlayDrawCommand>
        {
            new GOverlayFillRectangleCommand("header.background", bounds, background),
            new GOverlayTextCommand(
                "header.label",
                new(9, 4, 462, 20),
                "MEDIA SESSION",
                15,
                state.Light,
                background,
                GOverlayTextAlignment.Centre),
            new GOverlayLineCommand(
                "header.separator",
                0,
                bounds.Bottom - 1,
                bounds.Right - 1,
                bounds.Bottom - 1,
                GOverlayTheme.Divider)
        };
        return new("header", bounds, commands);
    }
}

public sealed class NowPlayingContentRegion : IGOverlayContentRegion
{
    public GOverlayRegionScene Compose(
        GOverlayDashboardState state,
        GOverlayRectangle bounds,
        DateTime localTime)
    {
        _ = localTime;
        var commands = new List<GOverlayDrawCommand>
        {
            new GOverlayFillRectangleCommand(
                "content.background",
                bounds,
                GOverlayTheme.Background),
            new GOverlayArtworkCommand(
                "content.artwork",
                new(10, 38, 200, 200),
                state.ArtworkKey,
                state.ArtworkContentType,
                state.ArtworkData,
                state.Dark,
                state.Light),
            new GOverlayLineCommand(
                "content.divider",
                219,
                38,
                219,
                238,
                GOverlayTheme.Divider),
            new GOverlayTextCommand(
                "content.title",
                new(231, 48, 239, 28),
                Truncate(state.Title, 28),
                19,
                state.Light,
                GOverlayTheme.Background),
            new GOverlayTextCommand(
                "content.artist",
                new(231, 80, 239, 23),
                Truncate(state.Artist, 34),
                15,
                state.Accent,
                GOverlayTheme.Background),
            new GOverlayTextCommand(
                "content.context",
                new(231, 108, 239, 20),
                Truncate(state.Context, 38),
                12,
                GOverlayTheme.Muted,
                GOverlayTheme.Background)
        };

        commands.Add(new GOverlayWaterfallCommand(
            "content.waterfall",
            GOverlayWaterfallGeometry.Bounds,
            state.WaterfallResetSequence,
            state.WaterfallColumnSequence,
            state.WaterfallWritePosition,
            state.WaterfallColumnWidth,
            state.HasWaterfallColumn,
            state.WaterfallBands,
            state.HasPalette,
            GOverlayTheme.Background,
            state.Dark,
            state.Dominant,
            state.Accent,
            state.Light));

        return new("content", bounds, commands);
    }

    private static string Truncate(string value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        if (value.Length <= maximumCharacters)
            return value;
        return value.Substring(0, maximumCharacters - 1) + "…";
    }
}

public sealed class PlaybackContextFooterRegion : IGOverlayFooterRegion
{
    public GOverlayRegionScene Compose(
        GOverlayDashboardState state,
        GOverlayRectangle bounds,
        DateTime localTime)
    {
        _ = localTime;
        var commands = new List<GOverlayDrawCommand>
        {
            new GOverlayFillRectangleCommand(
                "footer.background",
                bounds,
                GOverlayTheme.Background),
            new GOverlayLineCommand(
                "footer.separator",
                0,
                bounds.Y,
                bounds.Right - 1,
                bounds.Y,
                GOverlayTheme.Divider)
        };

        if (state.HasRating)
            AddRating(commands, state);
        else if (state.DurationSeconds > 0)
            AddTimeline(commands, state);

        AddPaletteItem(commands, "dominant", 9, state.Dominant);
        AddPaletteItem(commands, "accent", 126, state.Accent);
        AddPaletteItem(commands, "dark", 232, state.Dark);
        AddPaletteItem(commands, "light", 321, state.Light);
        return new("footer", bounds, commands);
    }

    private static void AddTimeline(
        ICollection<GOverlayDrawCommand> commands,
        GOverlayDashboardState state)
    {
        commands.Add(new GOverlayTextCommand(
            "footer.elapsed",
            new(9, 257, 57, 19),
            FormatDuration(state.PositionSeconds),
            12,
            state.Light,
            GOverlayTheme.Background));
        commands.Add(new GOverlayProgressCommand(
            "footer.progress",
            new(74, 264, 332, 5),
            state.Progress,
            state.Accent,
            GOverlayTheme.MeterBackground));
        commands.Add(new GOverlayTextCommand(
            "footer.duration",
            new(414, 257, 57, 19),
            FormatDuration(state.DurationSeconds),
            12,
            state.Light,
            GOverlayTheme.Background,
            GOverlayTextAlignment.Right));
    }

    private static void AddRating(
        ICollection<GOverlayDrawCommand> commands,
        GOverlayDashboardState state)
    {
        var positive = Math.Max(0, state.PositiveRatingCount);
        var negative = Math.Max(0, state.NegativeRatingCount);
        var total = positive + (double)negative;
        var positiveShare = total <= 0 ? 0 : positive / total;
        var negativeShare = total <= 0 ? 0 : negative / total;

        commands.Add(new GOverlayTextCommand(
            "footer.rating-positive-count",
            new(9, 257, 57, 19),
            "+" + FormatCount(positive),
            12,
            state.Accent,
            GOverlayTheme.Background));
        commands.Add(new GOverlayProgressCommand(
            "footer.rating-positive",
            new(74, 264, 164, 5),
            positiveShare,
            state.Accent,
            GOverlayTheme.MeterBackground,
            GOverlayProgressDirection.RightToLeft));
        commands.Add(new GOverlayProgressCommand(
            "footer.rating-negative",
            new(242, 264, 164, 5),
            negativeShare,
            GOverlayTheme.Negative,
            GOverlayTheme.MeterBackground));
        commands.Add(new GOverlayLineCommand(
            "footer.rating-centre",
            240,
            260,
            240,
            272,
            GOverlayTheme.Divider));
        commands.Add(new GOverlayTextCommand(
            "footer.rating-negative-count",
            new(414, 257, 57, 19),
            "-" + FormatCount(negative),
            12,
            GOverlayTheme.Negative,
            GOverlayTheme.Background,
            GOverlayTextAlignment.Right));
    }

    private static void AddPaletteItem(
        ICollection<GOverlayDrawCommand> commands,
        string label,
        int x,
        GOverlayColour colour)
    {
        commands.Add(new GOverlayFillRectangleCommand(
            "footer.palette." + label + ".swatch",
            new(x, 293, 11, 11),
            colour));
        commands.Add(new GOverlayTextCommand(
            "footer.palette." + label + ".label",
            new(x + 17, 289, 86, 19),
            label,
            11,
            GOverlayTheme.Muted,
            GOverlayTheme.Background));
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "--:--";

        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (value.TotalHours >= 1)
        {
            var hours = Math.Min(99, (int)value.TotalHours);
            var minutes = hours == 99 && value.TotalHours >= 100
                ? 59
                : value.Minutes;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1:00}",
                hours,
                minutes);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:00}",
            value.Minutes,
            value.Seconds);
    }

    private static string FormatCount(long value)
    {
        var count = Math.Max(0, value);
        if (count < 1_000)
            return count.ToString(CultureInfo.InvariantCulture);

        var (divisor, suffix) = count switch
        {
            >= 1_000_000_000 => (1_000_000_000d, "B"),
            >= 1_000_000 => (1_000_000d, "M"),
            _ => (1_000d, "K")
        };
        var scaled = count / divisor;
        return scaled >= 100
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0:0}{1}",
                scaled,
                suffix)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.#}{1}",
                scaled,
                suffix);
    }
}

internal static class GOverlayTheme
{
    public static readonly GOverlayColour Background = new(9, 12, 17);
    public static readonly GOverlayColour Divider = new(47, 55, 66);
    public static readonly GOverlayColour Muted = new(149, 160, 174);
    public static readonly GOverlayColour Inactive = new(92, 98, 108);
    public static readonly GOverlayColour MeterBackground = new(25, 31, 39);
    public static readonly GOverlayColour Negative = new(228, 92, 106);
    public static readonly GOverlayColour HardwareTitle = new(226, 232, 240);
    public static readonly GOverlayColour HardwareText = new(215, 222, 232);
    public static readonly GOverlayColour Gpu = new(57, 202, 255);
    public static readonly GOverlayColour Cpu = new(174, 117, 255);
}
