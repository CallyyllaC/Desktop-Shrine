using System.Text;

namespace DesktopShrine.Plugin.GOverlay.Layout;

public static class GOverlayBridgeCodec
{
    public const int ProtocolVersion = 10;
    private const int MaximumFrameBytes = 32 * 1024 * 1024;
    private const int MaximumHardwareMetrics = 16;

    public static void WriteFrame(
        Stream output,
        GOverlayDashboardState state,
        bool includeArtwork)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
        {
            writer.Write(ProtocolVersion);
            writer.Write(state.Revision);
            writer.Write(state.RenderGeneration);
            writer.Write(state.IsAvailable);
            writer.Write(state.PlaybackStatus ?? string.Empty);
            writer.Write(state.Title ?? string.Empty);
            writer.Write(state.Artist ?? string.Empty);
            writer.Write(state.Context ?? string.Empty);
            writer.Write(state.FontName ?? string.Empty);
            writer.Write(state.PositionSeconds);
            writer.Write(state.DurationSeconds);
            WriteColour(writer, state.Dominant);
            WriteColour(writer, state.Accent);
            WriteColour(writer, state.Dark);
            WriteColour(writer, state.Light);
            writer.Write(state.HasPalette);
            writer.Write(state.AudioActive);
            writer.Write(state.WaterfallResetSequence);
            writer.Write(state.WaterfallColumnSequence);
            writer.Write(state.WaterfallWritePosition);
            writer.Write(state.WaterfallColumnWidth);
            writer.Write(state.HasWaterfallColumn);
            writer.Write(state.WaterfallBands.Length);
            foreach (var value in state.WaterfallBands)
                writer.Write(value);
            writer.Write(state.ArtworkKey ?? string.Empty);
            WriteNullableString(writer, state.ArtworkContentType);
            writer.Write(includeArtwork);
            if (includeArtwork)
            {
                writer.Write(state.ArtworkData.Length);
                writer.Write(state.ArtworkData);
            }
            writer.Write(state.HasRating);
            writer.Write(state.PositiveRatingCount);
            writer.Write(state.NegativeRatingCount);
            WriteNullableString(writer, state.RatingSummary);
            writer.Write((int)state.Mode);
            writer.Write(state.HardwareProvider ?? string.Empty);
            writer.Write(state.HardwareProviderVersion ?? string.Empty);
            writer.Write(state.HardwareCapturedAtUnixMilliseconds);
            writer.Write(state.GpuName ?? string.Empty);
            writer.Write(state.CpuName ?? string.Empty);
            WriteHardwareMetrics(writer, state.GpuMetrics);
            WriteHardwareMetrics(writer, state.CpuMetrics);
            writer.Write(state.PhysicalMemorySummary ?? string.Empty);
            writer.Write(state.VirtualMemorySummary ?? string.Empty);
            writer.Write(state.PhysicalMemoryLevel);
            writer.Write(state.VirtualMemoryLevel);
            writer.Write(state.DontUseDrawPixels);
        }

        using var header = new BinaryWriter(output, Encoding.UTF8, true);
        header.Write(checked((int)payload.Length));
        payload.Position = 0;
        payload.CopyTo(output);
        output.Flush();
    }

    public static GOverlayDashboardState ReadFrame(
        Stream input,
        GOverlayDashboardState? previous = null)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, true);
        var length = reader.ReadInt32();
        if (length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("Invalid GOverlay bridge frame length.");

        var payload = reader.ReadBytes(length);
        if (payload.Length != length)
            throw new EndOfStreamException();

        using var buffer = new MemoryStream(payload, false);
        using var frame = new BinaryReader(buffer, Encoding.UTF8, true);
        var version = frame.ReadInt32();
        if (version is < 2 or > ProtocolVersion)
            throw new InvalidDataException(
                "Unsupported GOverlay bridge protocol version " + version + ".");

        var state = new GOverlayDashboardState
        {
            Revision = frame.ReadInt64(),
            RenderGeneration = version >= 7
                ? frame.ReadInt64()
                : 0,
            IsAvailable = frame.ReadBoolean(),
            PlaybackStatus = frame.ReadString(),
            Title = frame.ReadString(),
            Artist = frame.ReadString(),
            Context = frame.ReadString(),
            FontName = frame.ReadString(),
            PositionSeconds = frame.ReadDouble(),
            DurationSeconds = frame.ReadDouble(),
            Dominant = ReadColour(frame),
            Accent = ReadColour(frame),
            Dark = ReadColour(frame),
            Light = ReadColour(frame)
        };

        state.HasPalette = frame.ReadBoolean();
        state.AudioActive = frame.ReadBoolean();
        state.WaterfallResetSequence = frame.ReadInt64();
        state.WaterfallColumnSequence = frame.ReadInt64();
        state.WaterfallWritePosition = frame.ReadInt32();
        state.WaterfallColumnWidth = version >= 3
            ? GOverlayWaterfallGeometry.ValidateColumnWidth(frame.ReadInt32())
            : 3;
        state.HasWaterfallColumn = frame.ReadBoolean();
        var waterfallLength = frame.ReadInt32();
        if (waterfallLength is < 0
            or > GOverlayWaterfallGeometry.BandCount)
            throw new InvalidDataException(
                "Invalid waterfall band count.");
        state.WaterfallBands = new float[waterfallLength];
        for (var index = 0; index < waterfallLength; index++)
            state.WaterfallBands[index] = frame.ReadSingle();

        state.ArtworkKey = frame.ReadString();
        state.ArtworkContentType = ReadNullableString(frame);
        var includesArtwork = frame.ReadBoolean();
        if (includesArtwork)
        {
            var artworkLength = frame.ReadInt32();
            if (artworkLength is < 0 or > MaximumFrameBytes)
                throw new InvalidDataException("Invalid artwork length.");
            state.ArtworkData = frame.ReadBytes(artworkLength);
            if (state.ArtworkData.Length != artworkLength)
                throw new EndOfStreamException();
        }
        else if (previous is not null
                 && previous.ArtworkKey == state.ArtworkKey)
        {
            state.ArtworkData = previous.ArtworkData;
        }

        if (version >= 4)
        {
            state.HasRating = frame.ReadBoolean();
            state.PositiveRatingCount = frame.ReadInt64();
            state.NegativeRatingCount = frame.ReadInt64();
            state.RatingSummary = ReadNullableString(frame);
        }
        if (version >= 5)
        {
            var mode = frame.ReadInt32();
            if (!Enum.IsDefined(typeof(GOverlayDashboardMode), mode))
                throw new InvalidDataException(
                    "Invalid GOverlay dashboard mode.");
            state.Mode = (GOverlayDashboardMode)mode;
            state.HardwareProvider = frame.ReadString();
            state.HardwareProviderVersion = frame.ReadString();
            state.HardwareCapturedAtUnixMilliseconds = frame.ReadInt64();
            state.GpuName = frame.ReadString();
            state.CpuName = frame.ReadString();
            state.GpuMetrics = ReadHardwareMetrics(frame);
            state.CpuMetrics = ReadHardwareMetrics(frame);
            if (version >= 6)
            {
                state.PhysicalMemorySummary = frame.ReadString();
                state.VirtualMemorySummary = frame.ReadString();
                if (version >= 8)
                {
                    state.PhysicalMemoryLevel = frame.ReadDouble();
                    state.VirtualMemoryLevel = frame.ReadDouble();
                }
            }
        }
        if (version == 9)
        {
            var legacyMode = frame.ReadInt32();
            if (legacyMode is < 0 or > 1)
                throw new InvalidDataException(
                    "Invalid legacy GOverlay compatibility value.");
            state.DontUseDrawPixels = legacyMode == 0;
            _ = frame.ReadString();
            _ = frame.ReadInt32();
            _ = frame.ReadInt32();
            _ = frame.ReadInt32();
            _ = frame.ReadInt32();
            _ = frame.ReadInt32();
        }
        else if (version >= 10)
            state.DontUseDrawPixels = frame.ReadBoolean();

        return state;
    }

    private static void WriteHardwareMetrics(
        BinaryWriter writer,
        IReadOnlyList<GOverlayHardwareMetricState>? metrics)
    {
        metrics ??= Array.Empty<GOverlayHardwareMetricState>();
        if (metrics.Count > MaximumHardwareMetrics)
            throw new InvalidDataException(
                "Too many GOverlay hardware metrics.");
        writer.Write(metrics.Count);
        foreach (var metric in metrics)
        {
            writer.Write(metric.Label ?? string.Empty);
            writer.Write(metric.DisplayValue ?? string.Empty);
            writer.Write(metric.Level);
            writer.Write(metric.IsAvailable);
        }
    }

    private static GOverlayHardwareMetricState[] ReadHardwareMetrics(
        BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > MaximumHardwareMetrics)
            throw new InvalidDataException(
                "Invalid GOverlay hardware metric count.");
        var result = new GOverlayHardwareMetricState[count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = new()
            {
                Label = reader.ReadString(),
                DisplayValue = reader.ReadString(),
                Level = reader.ReadDouble(),
                IsAvailable = reader.ReadBoolean()
            };
        }
        return result;
    }

    private static void WriteColour(BinaryWriter writer, GOverlayColour colour)
    {
        writer.Write(colour.Red);
        writer.Write(colour.Green);
        writer.Write(colour.Blue);
    }

    private static GOverlayColour ReadColour(BinaryReader reader) =>
        new(reader.ReadByte(), reader.ReadByte(), reader.ReadByte());

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
            writer.Write(value);
    }

    private static string? ReadNullableString(BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadString() : null;
}
