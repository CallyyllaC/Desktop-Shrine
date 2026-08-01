using System.Globalization;

namespace DesktopShrine.Plugin.GOverlay.Layout;

public readonly struct GOverlayColour : IEquatable<GOverlayColour>
{
    public GOverlayColour(byte red, byte green, byte blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    public byte Red { get; }
    public byte Green { get; }
    public byte Blue { get; }
    public string Hex => string.Format(
        CultureInfo.InvariantCulture,
        "#{0:X2}{1:X2}{2:X2}",
        Red,
        Green,
        Blue);
    public int Rgb565 =>
        ((Red & 0xF8) << 8)
        | ((Green & 0xFC) << 3)
        | (Blue >> 3);

    public static GOverlayColour Interpolate(
        GOverlayColour from,
        GOverlayColour to,
        double amount)
    {
        var value = Math.Max(0, Math.Min(1, amount));
        return new(
            (byte)Math.Round(from.Red + ((to.Red - from.Red) * value)),
            (byte)Math.Round(from.Green + ((to.Green - from.Green) * value)),
            (byte)Math.Round(from.Blue + ((to.Blue - from.Blue) * value)));
    }

    public bool Equals(GOverlayColour other) =>
        Red == other.Red && Green == other.Green && Blue == other.Blue;

    public override bool Equals(object? value) =>
        value is GOverlayColour other && Equals(other);

    public override int GetHashCode() =>
        (Red << 16) | (Green << 8) | Blue;
}

public readonly struct GOverlayRectangle : IEquatable<GOverlayRectangle>
{
    public GOverlayRectangle(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Equals(GOverlayRectangle other) =>
        X == other.X
        && Y == other.Y
        && Width == other.Width
        && Height == other.Height;

    public override bool Equals(object? value) =>
        value is GOverlayRectangle other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + X;
            hash = hash * 31 + Y;
            hash = hash * 31 + Width;
            hash = hash * 31 + Height;
            return hash;
        }
    }
}

public enum GOverlayTextAlignment
{
    Left = -1,
    Centre = 0,
    Right = 1
}

public abstract class GOverlayDrawCommand
{
    protected GOverlayDrawCommand(string key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public string Key { get; }
    public abstract string Fingerprint { get; }
}

public sealed class GOverlayFillRectangleCommand : GOverlayDrawCommand
{
    public GOverlayFillRectangleCommand(
        string key,
        GOverlayRectangle bounds,
        GOverlayColour colour) : base(key)
    {
        Bounds = bounds;
        Colour = colour;
    }

    public GOverlayRectangle Bounds { get; }
    public GOverlayColour Colour { get; }
    public override string Fingerprint =>
        string.Format(
            CultureInfo.InvariantCulture,
            "fill:{0},{1},{2},{3}:{4}",
            Bounds.X,
            Bounds.Y,
            Bounds.Width,
            Bounds.Height,
            Colour.Hex);
}

public sealed class GOverlayStrokeRectangleCommand : GOverlayDrawCommand
{
    public GOverlayStrokeRectangleCommand(
        string key,
        GOverlayRectangle bounds,
        GOverlayColour colour,
        int thickness = 1) : base(key)
    {
        Bounds = bounds;
        Colour = colour;
        Thickness = thickness;
    }

    public GOverlayRectangle Bounds { get; }
    public GOverlayColour Colour { get; }
    public int Thickness { get; }
    public override string Fingerprint =>
        string.Format(
            CultureInfo.InvariantCulture,
            "stroke:{0},{1},{2},{3}:{4}:{5}",
            Bounds.X,
            Bounds.Y,
            Bounds.Width,
            Bounds.Height,
            Colour.Hex,
            Thickness);
}

public sealed class GOverlayLineCommand : GOverlayDrawCommand
{
    public GOverlayLineCommand(
        string key,
        int x1,
        int y1,
        int x2,
        int y2,
        GOverlayColour colour) : base(key)
    {
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        Colour = colour;
    }

    public int X1 { get; }
    public int Y1 { get; }
    public int X2 { get; }
    public int Y2 { get; }
    public GOverlayColour Colour { get; }
    public override string Fingerprint =>
        string.Format(
            CultureInfo.InvariantCulture,
            "line:{0},{1},{2},{3}:{4}",
            X1,
            Y1,
            X2,
            Y2,
            Colour.Hex);
}

public sealed class GOverlayTextCommand : GOverlayDrawCommand
{
    public GOverlayTextCommand(
        string key,
        GOverlayRectangle bounds,
        string text,
        int fontSize,
        GOverlayColour colour,
        GOverlayColour background,
        GOverlayTextAlignment alignment = GOverlayTextAlignment.Left)
        : base(key)
    {
        Bounds = bounds;
        Text = text ?? string.Empty;
        FontSize = fontSize;
        Colour = colour;
        Background = background;
        Alignment = alignment;
    }

    public GOverlayRectangle Bounds { get; }
    public string Text { get; }
    public int FontSize { get; }
    public GOverlayColour Colour { get; }
    public GOverlayColour Background { get; }
    public GOverlayTextAlignment Alignment { get; }
    public override string Fingerprint =>
        string.Format(
            CultureInfo.InvariantCulture,
            "text:{0},{1},{2},{3}:{4}:{5}:{6}:{7}:{8}",
            Bounds.X,
            Bounds.Y,
            Bounds.Width,
            Bounds.Height,
            Text,
            FontSize,
            Colour.Hex,
            Background.Hex,
            (int)Alignment);
}

public sealed class GOverlayArtworkCommand : GOverlayDrawCommand
{
    public GOverlayArtworkCommand(
        string key,
        GOverlayRectangle bounds,
        string artworkKey,
        string? contentType,
        byte[] artworkData,
        GOverlayColour background,
        GOverlayColour foreground) : base(key)
    {
        Bounds = bounds;
        ArtworkKey = artworkKey ?? string.Empty;
        ContentType = contentType;
        ArtworkData = artworkData ?? Array.Empty<byte>();
        Background = background;
        Foreground = foreground;
    }

    public GOverlayRectangle Bounds { get; }
    public string ArtworkKey { get; }
    public string? ContentType { get; }
    public byte[] ArtworkData { get; }
    public GOverlayColour Background { get; }
    public GOverlayColour Foreground { get; }
    public bool HasArtwork => ArtworkData.Length > 0;
    public override string Fingerprint =>
        string.Format(
            CultureInfo.InvariantCulture,
            "art:{0},{1},{2},{3}:{4}:{5}:{6}",
            Bounds.X,
            Bounds.Y,
            Bounds.Width,
            Bounds.Height,
            ArtworkKey,
            Background.Hex,
            Foreground.Hex);
}

public sealed class GOverlayMeterBarCommand : GOverlayDrawCommand
{
    public GOverlayMeterBarCommand(
        string key,
        GOverlayRectangle bounds,
        double value,
        GOverlayColour foreground,
        GOverlayColour background) : base(key)
    {
        Bounds = bounds;
        Value = Math.Max(0, Math.Min(1, value));
        Foreground = foreground;
        Background = background;
    }

    public GOverlayRectangle Bounds { get; }
    public double Value { get; }
    public GOverlayColour Foreground { get; }
    public GOverlayColour Background { get; }
    public override string Fingerprint =>
        string.Format(
            CultureInfo.InvariantCulture,
            "meter:{0},{1},{2},{3}:{4:F3}:{5}:{6}",
            Bounds.X,
            Bounds.Y,
            Bounds.Width,
            Bounds.Height,
            Value,
            Foreground.Hex,
            Background.Hex);
}

public static class GOverlayWaterfallGeometry
{
    public const int BandCount = 8;
    public const int ColumnWidth = 4;
    public static readonly GOverlayRectangle Bounds =
        new(231, 148, 237, 80);
    public static int ColumnCount => Bounds.Width / ColumnWidth;

    public static int ValidateColumnWidth(int columnWidth)
    {
        if (columnWidth is < 1 or > 16)
            throw new ArgumentOutOfRangeException(
                nameof(columnWidth),
                "Waterfall column width must be between 1 and 16 pixels.");
        return columnWidth;
    }

    public static int ColumnCountFor(int columnWidth) =>
        Bounds.Width / ValidateColumnWidth(columnWidth);

    public static IReadOnlyList<int> SkippedPositions(
        int previousPosition,
        long sequenceDelta,
        int columnCount)
    {
        if (previousPosition < 0
            || previousPosition >= columnCount
            || sequenceDelta <= 1
            || sequenceDelta >= columnCount)
            return Array.Empty<int>();

        var positions = new int[(int)sequenceDelta - 1];
        for (var offset = 1; offset < sequenceDelta; offset++)
            positions[offset - 1] =
                (previousPosition + offset) % columnCount;
        return positions;
    }

    public static IReadOnlyList<int> SkippedPositions(
        int previousPosition,
        long sequenceDelta) =>
        SkippedPositions(previousPosition, sequenceDelta, ColumnCount);
}

public sealed class GOverlayWaterfallCommand : GOverlayDrawCommand
{
    public GOverlayWaterfallCommand(
        string key,
        GOverlayRectangle bounds,
        long resetSequence,
        long columnSequence,
        int writePosition,
        int columnWidth,
        bool hasColumn,
        IReadOnlyList<float> bands,
        bool hasPalette,
        GOverlayColour background,
        GOverlayColour dark,
        GOverlayColour dominant,
        GOverlayColour accent,
        GOverlayColour light) : base(key)
    {
        Bounds = bounds;
        ResetSequence = resetSequence;
        ColumnSequence = columnSequence;
        ColumnWidth =
            GOverlayWaterfallGeometry.ValidateColumnWidth(columnWidth);
        ColumnCount =
            GOverlayWaterfallGeometry.ColumnCountFor(ColumnWidth);
        WritePosition = Math.Max(
            0,
            Math.Min(
                ColumnCount - 1,
                writePosition));
        HasColumn = hasColumn;
        Bands = bands ?? Array.Empty<float>();
        HasPalette = hasPalette;
        Background = background;
        Dark = dark;
        Dominant = dominant;
        Accent = accent;
        Light = light;
    }

    public GOverlayRectangle Bounds { get; }
    public long ResetSequence { get; }
    public long ColumnSequence { get; }
    public int WritePosition { get; }
    public int ColumnWidth { get; }
    public int ColumnCount { get; }
    public bool HasColumn { get; }
    public IReadOnlyList<float> Bands { get; }
    public bool HasPalette { get; }
    public GOverlayColour Background { get; }
    public GOverlayColour Dark { get; }
    public GOverlayColour Dominant { get; }
    public GOverlayColour Accent { get; }
    public GOverlayColour Light { get; }
    public GOverlayRectangle WriteCursorBounds
    {
        get
        {
            var columnX = Bounds.X + (WritePosition * ColumnWidth);
            return new(
                columnX + ColumnWidth - 1,
                Bounds.Y,
                1,
                Bounds.Height);
        }
    }
    public GOverlayColour WriteCursorColour => ColourFor(1);
    public override string Fingerprint =>
        string.Format(
            CultureInfo.InvariantCulture,
            "waterfall:{0}:{1}:{2}:{3}:{4}",
            ResetSequence,
            ColumnSequence,
            WritePosition,
            ColumnWidth,
            HasColumn);

    public GOverlayColour ColourFor(double energy) =>
        GOverlayWaterfallPalette.Map(
            energy,
            HasPalette,
            Background,
            Dark,
            Dominant,
            Accent,
            Light);
}

public static class GOverlayWaterfallPalette
{
    private static readonly GOverlayColour FallbackDark =
        new(4, 11, 24);
    private static readonly GOverlayColour FallbackDominant =
        new(18, 72, 112);
    private static readonly GOverlayColour FallbackAccent =
        new(38, 203, 220);
    private static readonly GOverlayColour FallbackLight =
        new(239, 252, 255);

    public static GOverlayColour Map(
        double energy,
        bool hasPalette,
        GOverlayColour background,
        GOverlayColour dark,
        GOverlayColour dominant,
        GOverlayColour accent,
        GOverlayColour light)
    {
        if (!hasPalette)
        {
            dark = FallbackDark;
            dominant = FallbackDominant;
            accent = FallbackAccent;
            light = FallbackLight;
        }

        var value = Math.Max(0, Math.Min(1, energy));
        if (value <= 0.08)
            return GOverlayColour.Interpolate(
                background,
                dark,
                value / 0.08);
        if (value <= 0.40)
            return GOverlayColour.Interpolate(
                dark,
                dominant,
                (value - 0.08) / 0.32);
        if (value <= 0.70)
            return GOverlayColour.Interpolate(
                dominant,
                accent,
                (value - 0.40) / 0.30);
        return GOverlayColour.Interpolate(
            accent,
            light,
            (value - 0.70) / 0.30);
    }
}

public enum GOverlayProgressDirection
{
    LeftToRight,
    RightToLeft
}

public sealed class GOverlayProgressCommand : GOverlayDrawCommand
{
    public GOverlayProgressCommand(
        string key,
        GOverlayRectangle bounds,
        double value,
        GOverlayColour foreground,
        GOverlayColour background,
        GOverlayProgressDirection direction =
            GOverlayProgressDirection.LeftToRight) : base(key)
    {
        Bounds = bounds;
        Value = Math.Max(0, Math.Min(1, value));
        Foreground = foreground;
        Background = background;
        Direction = direction;
    }

    public GOverlayRectangle Bounds { get; }
    public double Value { get; }
    public GOverlayColour Foreground { get; }
    public GOverlayColour Background { get; }
    public GOverlayProgressDirection Direction { get; }
    public override string Fingerprint =>
        string.Format(
            CultureInfo.InvariantCulture,
            "progress:{0},{1},{2},{3}:{4:F4}:{5}:{6}:{7}",
            Bounds.X,
            Bounds.Y,
            Bounds.Width,
            Bounds.Height,
            Value,
            Foreground.Hex,
            Background.Hex,
            Direction);
}

public sealed class GOverlayRegionScene
{
    public GOverlayRegionScene(
        string id,
        GOverlayRectangle bounds,
        IReadOnlyList<GOverlayDrawCommand> commands)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Bounds = bounds;
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    public string Id { get; }
    public GOverlayRectangle Bounds { get; }
    public IReadOnlyList<GOverlayDrawCommand> Commands { get; }
}

public sealed class GOverlayDashboardScene
{
    public const int PixelWidth = 480;
    public const int PixelHeight = 320;

    public GOverlayDashboardScene(
        GOverlayRegionScene header,
        GOverlayRegionScene content,
        GOverlayRegionScene footer)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Footer = footer ?? throw new ArgumentNullException(nameof(footer));
    }

    public GOverlayRegionScene Header { get; }
    public GOverlayRegionScene Content { get; }
    public GOverlayRegionScene Footer { get; }
    public IEnumerable<GOverlayRegionScene> Regions
    {
        get
        {
            yield return Header;
            yield return Content;
            yield return Footer;
        }
    }
}
