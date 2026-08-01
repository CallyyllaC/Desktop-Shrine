using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using DesktopShrine.Plugin.GOverlay.Layout;
using GOverlayPlugin.Interfaces;

namespace DesktopShrine.Plugin.GOverlay.LegacySdk;

internal sealed class GOverlaySdkSceneRenderer
{
    // GOverlay's own live-image renderer submits 40 pixels per USB request.
    // Larger requests are silently dropped by the LCDSysInfo 2.0 transport.
    private const int PixelBatchSize = 40;
    // Keep each callback bounded so metadata, progress and waterfall work can
    // continue while a large image is being transferred.
    private const int ArtworkBatchesPerRender = 4;
    private readonly Dictionary<string, string> fingerprints =
        new(StringComparer.Ordinal);
    private Task<PreparedArtwork>? artworkPreparation;
    private PreparedArtwork? artworkTransfer;
    private string requestedArtworkFingerprint = string.Empty;
    private string displayedArtworkFingerprint = string.Empty;
    private string failedArtworkFingerprint = string.Empty;
    private bool hasDisplayedArtwork;
    private long waterfallResetSequence = long.MinValue;
    private long waterfallColumnSequence = long.MinValue;
    private int waterfallWritePosition = -1;
    private float[] waterfallCursorBands = Array.Empty<float>();

    public void ResetForFullRedraw()
    {
        fingerprints.Clear();
        artworkPreparation = null;
        artworkTransfer = null;
        requestedArtworkFingerprint = string.Empty;
        displayedArtworkFingerprint = string.Empty;
        failedArtworkFingerprint = string.Empty;
        hasDisplayedArtwork = false;
        waterfallResetSequence = long.MinValue;
        waterfallColumnSequence = long.MinValue;
        waterfallWritePosition = -1;
        waterfallCursorBands = Array.Empty<float>();
    }

    public long? Render(
        IHost host,
        GOverlayDashboardScene scene,
        int cacheRuns,
        Func<bool> isPriorityStateCurrent)
    {
        // GOverlay resets cacheRuns to zero at its configured cache interval.
        // That is a hint to refresh cached values, not an indication that the
        // physical LCD was cleared. Clearing here caused a full-screen flash
        // and forced expensive artwork uploads on every reset.
        _ = cacheRuns;

        GOverlayArtworkCommand? artwork = null;
        GOverlayWaterfallCommand? waterfall = null;
        foreach (var region in scene.Regions)
            foreach (var command in region.Commands)
            {
                if (command is GOverlayArtworkCommand artworkCommand)
                {
                    artwork = artworkCommand;
                    continue;
                }
                if (command is GOverlayWaterfallCommand waterfallCommand)
                {
                    waterfall = waterfallCommand;
                    continue;
                }

                RenderChangedCommand(host, command, scene);
            }

        if (artwork is not null)
        {
            _ = RenderArtwork(
                host,
                artwork,
                scene,
                isPriorityStateCurrent);
        }

        if (waterfall is not null)
            return RenderWaterfall(host, waterfall);

        return null;
    }

    private void RenderChangedCommand(
        IHost host,
        GOverlayDrawCommand command,
        GOverlayDashboardScene scene)
    {
        if (fingerprints.TryGetValue(
                command.Key,
                out var previous)
            && previous == command.Fingerprint)
            return;

        RenderCommand(host, command, scene);
        fingerprints[command.Key] = command.Fingerprint;
    }

    private static void RenderCommand(
        IHost host,
        GOverlayDrawCommand command,
        GOverlayDashboardScene scene)
    {
        switch (command)
        {
            case GOverlayFillRectangleCommand fill:
                Fill(host, fill.Bounds, fill.Colour);
                break;
            case GOverlayStrokeRectangleCommand stroke:
                host.LCDSys2_Draw_Rectangle(
                    stroke.Bounds.X,
                    stroke.Bounds.Y,
                    stroke.Bounds.Right - 1,
                    stroke.Bounds.Bottom - 1,
                    stroke.Colour.Rgb565,
                    stroke.Thickness,
                    0);
                break;
            case GOverlayLineCommand line:
                Line(host, line);
                break;
            case GOverlayTextCommand text:
                Text(host, text, scene);
                break;
            case GOverlayMeterBarCommand meter:
                Meter(host, meter);
                break;
            case GOverlayProgressCommand progress:
                Progress(host, progress);
                break;
        }
    }

    private static void Text(
        IHost host,
        GOverlayTextCommand text,
        GOverlayDashboardScene scene)
    {
        var font = FindFont(scene);
        host.LCDSys2_Draw_Text_Font(
            text.Bounds.X,
            text.Bounds.Y,
            text.Text,
            text.Bounds.Width,
            text.Colour.Rgb565,
            text.Background.Rgb565,
            font,
            0,
            (int)text.Alignment,
            string.Empty,
            0);
    }

    private bool RenderArtwork(
        IHost host,
        GOverlayArtworkCommand artwork,
        GOverlayDashboardScene scene,
        Func<bool> isPriorityStateCurrent)
    {
        if (artwork.Fingerprint != requestedArtworkFingerprint)
        {
            requestedArtworkFingerprint = artwork.Fingerprint;
            failedArtworkFingerprint = string.Empty;
            artworkPreparation = null;
            artworkTransfer = null;

            if (!artwork.HasArtwork)
            {
                Placeholder(host, artwork, scene);
                displayedArtworkFingerprint = artwork.Fingerprint;
                hasDisplayedArtwork = false;
                return false;
            }

            // Keep the previous image visible while the replacement is decoded
            // and resized. On the first image, establish a useful placeholder.
            if (!hasDisplayedArtwork)
                Placeholder(host, artwork, scene);

            artworkPreparation = Task.Run(() => PrepareArtwork(artwork));
        }

        if (!artwork.HasArtwork
            || displayedArtworkFingerprint == artwork.Fingerprint
            || failedArtworkFingerprint == artwork.Fingerprint)
            return false;

        if (artworkTransfer is null)
        {
            if (artworkPreparation is null)
            {
                artworkPreparation =
                    Task.Run(() => PrepareArtwork(artwork));
                host.DebugMessage(
                    "Desktop Shrine - recovered missing artwork preparation");
                return true;
            }

            if (!artworkPreparation.IsCompleted)
                return true;

            if (artworkPreparation.IsFaulted)
            {
                var error = artworkPreparation.Exception!
                    .GetBaseException()
                    .Message;
                host.DebugMessage(
                    "Desktop Shrine - artwork preparation failed: " + error);
                failedArtworkFingerprint = artwork.Fingerprint;
                artworkPreparation = null;
                Placeholder(host, artwork, scene);
                displayedArtworkFingerprint = artwork.Fingerprint;
                hasDisplayedArtwork = false;
                return false;
            }

            if (!isPriorityStateCurrent())
                return CancelArtworkTransfer(host);

            artworkTransfer = artworkPreparation.Result;
            artworkPreparation = null;
            // A cancelled upload can leave pixels from several tracks in this
            // region. One cheap rectangle reset gives the replacement a clean
            // canvas without touching metadata, footer or waterfall history.
            Fill(host, artwork.Bounds, artwork.Background);
            host.DebugMessage(
                $"Desktop Shrine - transferring {artworkTransfer.Bounds.Width}x{artworkTransfer.Bounds.Height} opaque artwork into {artwork.Bounds.Width}x{artwork.Bounds.Height} from {artwork.ArtworkData.Length} bytes");
        }

        // Transfer a bounded slice at native GOverlay packet size. The next
        // display callback resumes from NextPixel, while newer state can cancel
        // the transfer between any two packets.
        var batches = 0;
        while (artworkTransfer.NextPixel < artworkTransfer.Colours.Length
               && batches < ArtworkBatchesPerRender)
        {
            if (!isPriorityStateCurrent())
                return CancelArtworkTransfer(host);

            var count = Math.Min(
                PixelBatchSize,
                artworkTransfer.Colours.Length - artworkTransfer.NextPixel);
            var pixels = new ArrayList(count);
            for (var index = 0; index < count; index++)
            {
                var pixelIndex = artworkTransfer.NextPixel++;
                pixels.Add(new ArrayList
                {
                    artworkTransfer.Bounds.X
                        + pixelIndex % artworkTransfer.Bounds.Width,
                    artworkTransfer.Bounds.Y
                        + pixelIndex / artworkTransfer.Bounds.Width,
                    artworkTransfer.Colours[pixelIndex]
                });
            }
            host.LCDSys2_Draw_Pixels(pixels);
            batches++;

            // The bridge receives new state on its own thread while this
            // synchronous SDK call is in progress. Checking again here also
            // catches a change that arrived during the final USB packet.
            if (!isPriorityStateCurrent())
                return CancelArtworkTransfer(host);
        }

        if (artworkTransfer.NextPixel < artworkTransfer.Colours.Length)
            return true;

        displayedArtworkFingerprint = artwork.Fingerprint;
        hasDisplayedArtwork = true;
        artworkTransfer = null;
        host.DebugMessage("Desktop Shrine - artwork transfer complete");
        return false;
    }

    private bool CancelArtworkTransfer(IHost host)
    {
        artworkTransfer = null;
        artworkPreparation = null;
        // The same artwork fingerprint may still be current when a late
        // duration/status update triggered the old cancellation path. Never
        // leave it marked as requested without either a preparation or
        // transfer to resume.
        requestedArtworkFingerprint = string.Empty;
        displayedArtworkFingerprint = string.Empty;
        hasDisplayedArtwork = false;
        host.DebugMessage(
            "Desktop Shrine - cancelled stale artwork transfer");

        // Keep meters behind the new priority state. Returning immediately
        // lets GOverlay invoke us again so the latest metadata is drawn first.
        return true;
    }

    private static PreparedArtwork PrepareArtwork(
        GOverlayArtworkCommand artwork)
    {
        using var sourceStream = new MemoryStream(artwork.ArtworkData, false);
        using var source = Image.FromStream(sourceStream);
        var sourceBounds = FindArtworkContentBounds(source);
        var imageBounds = GOverlayArtworkSizing.Contain(
            sourceBounds.Width,
            sourceBounds.Height,
            artwork.Bounds);
        using var scaled = new Bitmap(
            imageBounds.Width,
            imageBounds.Height,
            PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            // Flatten alpha into the dashboard background. The USB transfer
            // therefore contains only opaque RGB565 pixels; GOverlay never has
            // to perform its particularly slow transparency path.
            graphics.Clear(Color.FromArgb(
                artwork.Background.Red,
                artwork.Background.Green,
                artwork.Background.Blue));
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            graphics.DrawImage(
                source,
                new Rectangle(0, 0, scaled.Width, scaled.Height),
                sourceBounds,
                GraphicsUnit.Pixel);
        }

        var colours = new int[scaled.Width * scaled.Height];
        for (var y = 0; y < scaled.Height; y++)
            for (var x = 0; x < scaled.Width; x++)
            {
                var colour = scaled.GetPixel(x, y);
                colours[y * scaled.Width + x] = new GOverlayColour(
                    colour.R,
                    colour.G,
                    colour.B).Rgb565;
            }

        return new(imageBounds, colours);
    }

    private static Rectangle FindArtworkContentBounds(Image source)
    {
        const int maximumSampleDimension = 256;
        var scale = Math.Min(
            1d,
            maximumSampleDimension
                / (double)Math.Max(source.Width, source.Height));
        var sampleWidth = Math.Max(
            1,
            (int)Math.Round(source.Width * scale));
        var sampleHeight = Math.Max(
            1,
            (int)Math.Round(source.Height * scale));
        using var sample = new Bitmap(
            sampleWidth,
            sampleHeight,
            PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(sample))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode =
                InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, sampleWidth, sampleHeight),
                new Rectangle(0, 0, source.Width, source.Height),
                GraphicsUnit.Pixel);
        }

        var full = new Rectangle(0, 0, sampleWidth, sampleHeight);
        var alphaBounds = FindAlphaBounds(sample);
        if (alphaBounds.IsEmpty)
            return new Rectangle(0, 0, source.Width, source.Height);

        var sampleBounds = alphaBounds != full
            ? alphaBounds
            : FindUniformMatteBounds(sample, full);
        return ScaleBoundsToSource(
            sampleBounds,
            sampleWidth,
            sampleHeight,
            source.Width,
            source.Height);
    }

    private static Rectangle FindAlphaBounds(Bitmap image)
    {
        const byte alphaThreshold = 16;
        var left = image.Width;
        var top = image.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                if (image.GetPixel(x, y).A < alphaThreshold)
                    continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }

        return right < left || bottom < top
            ? Rectangle.Empty
            : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static Rectangle FindUniformMatteBounds(
        Bitmap image,
        Rectangle bounds)
    {
        const int cornerToleranceSquared = 30 * 30;
        const int foregroundToleranceSquared = 42 * 42;
        var corners = new[]
        {
            image.GetPixel(bounds.Left, bounds.Top),
            image.GetPixel(bounds.Right - 1, bounds.Top),
            image.GetPixel(bounds.Left, bounds.Bottom - 1),
            image.GetPixel(bounds.Right - 1, bounds.Bottom - 1)
        };
        for (var first = 0; first < corners.Length; first++)
            for (var second = first + 1;
                 second < corners.Length;
                 second++)
            {
                if (ColourDistanceSquared(
                        corners[first],
                        corners[second])
                    > cornerToleranceSquared)
                    return bounds;
            }

        var background = Color.FromArgb(
            (int)Math.Round(corners.Average(colour => colour.R)),
            (int)Math.Round(corners.Average(colour => colour.G)),
            (int)Math.Round(corners.Average(colour => colour.B)));
        var minimumColumnPixels = Math.Max(
            2,
            (int)Math.Ceiling(bounds.Height * 0.06));
        var minimumRowPixels = Math.Max(
            2,
            (int)Math.Ceiling(bounds.Width * 0.06));
        var left = bounds.Right;
        var right = bounds.Left - 1;
        for (var x = bounds.Left; x < bounds.Right; x++)
        {
            var foreground = 0;
            for (var y = bounds.Top; y < bounds.Bottom; y++)
            {
                if (ColourDistanceSquared(
                        image.GetPixel(x, y),
                        background)
                    > foregroundToleranceSquared)
                    foreground++;
            }
            if (foreground < minimumColumnPixels)
                continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
        }

        var top = bounds.Bottom;
        var bottom = bounds.Top - 1;
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            var foreground = 0;
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                if (ColourDistanceSquared(
                        image.GetPixel(x, y),
                        background)
                    > foregroundToleranceSquared)
                    foreground++;
            }
            if (foreground < minimumRowPixels)
                continue;
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }

        if (right < left || bottom < top)
            return bounds;

        var candidate = Rectangle.FromLTRB(
            Math.Max(bounds.Left, left - 1),
            Math.Max(bounds.Top, top - 1),
            Math.Min(bounds.Right, right + 2),
            Math.Min(bounds.Bottom, bottom + 2));
        var retainsEnoughContent =
            candidate.Width >= bounds.Width * 0.25
            && candidate.Height >= bounds.Height * 0.25;
        var removesMeaningfulMatte =
            candidate.Width <= bounds.Width * 0.95
            || candidate.Height <= bounds.Height * 0.95;
        return retainsEnoughContent && removesMeaningfulMatte
            ? candidate
            : bounds;
    }

    private static Rectangle ScaleBoundsToSource(
        Rectangle sample,
        int sampleWidth,
        int sampleHeight,
        int sourceWidth,
        int sourceHeight)
    {
        var left = (int)Math.Floor(
            sample.Left * sourceWidth / (double)sampleWidth);
        var top = (int)Math.Floor(
            sample.Top * sourceHeight / (double)sampleHeight);
        var right = (int)Math.Ceiling(
            sample.Right * sourceWidth / (double)sampleWidth);
        var bottom = (int)Math.Ceiling(
            sample.Bottom * sourceHeight / (double)sampleHeight);
        return Rectangle.FromLTRB(
            Math.Max(0, Math.Min(sourceWidth - 1, left)),
            Math.Max(0, Math.Min(sourceHeight - 1, top)),
            Math.Max(1, Math.Min(sourceWidth, right)),
            Math.Max(1, Math.Min(sourceHeight, bottom)));
    }

    private static int ColourDistanceSquared(Color first, Color second)
    {
        var red = first.R - second.R;
        var green = first.G - second.G;
        var blue = first.B - second.B;
        return (red * red) + (green * green) + (blue * blue);
    }

    private static void Placeholder(
        IHost host,
        GOverlayArtworkCommand artwork,
        GOverlayDashboardScene scene)
    {
        Fill(host, artwork.Bounds, artwork.Background);
        Line(
            host,
            new(
                "placeholder.one",
                artwork.Bounds.X,
                artwork.Bounds.Y,
                artwork.Bounds.Right - 1,
                artwork.Bounds.Bottom - 1,
                artwork.Foreground));
        Line(
            host,
            new(
                "placeholder.two",
                artwork.Bounds.Right - 1,
                artwork.Bounds.Y,
                artwork.Bounds.X,
                artwork.Bounds.Bottom - 1,
                artwork.Foreground));
        Text(
            host,
            new(
                "placeholder.text",
                new(
                    artwork.Bounds.X,
                    artwork.Bounds.Y + artwork.Bounds.Height / 2 - 12,
                    artwork.Bounds.Width,
                    24),
                "ALBUM ART",
                13,
                artwork.Foreground,
                artwork.Background,
                GOverlayTextAlignment.Centre),
            scene);
    }

    private static void Meter(
        IHost host,
        GOverlayMeterBarCommand meter)
    {
        Fill(host, meter.Bounds, meter.Background);
        var height = Math.Max(
            1,
            (int)Math.Round(meter.Bounds.Height * meter.Value));
        Fill(
            host,
            new(
                meter.Bounds.X,
                meter.Bounds.Bottom - height,
                meter.Bounds.Width,
                height),
            meter.Foreground);
    }

    private long? RenderWaterfall(
        IHost host,
        GOverlayWaterfallCommand waterfall)
    {
        if (waterfall.ResetSequence != waterfallResetSequence)
        {
            Fill(
                host,
                waterfall.Bounds,
                waterfall.Background);
            waterfallResetSequence = waterfall.ResetSequence;
            waterfallColumnSequence = long.MinValue;
            waterfallWritePosition = -1;
            waterfallCursorBands = Array.Empty<float>();
        }

        if (!waterfall.HasColumn)
            return null;

        if (waterfall.ColumnSequence <= waterfallColumnSequence)
            return waterfall.ColumnSequence;

        RestorePreviousWaterfallCursor(host, waterfall);
        ClearSkippedWaterfallColumns(host, waterfall);
        DrawWaterfallColumn(
            host,
            waterfall,
            waterfall.WritePosition,
            waterfall.Bands);
        Fill(
            host,
            waterfall.WriteCursorBounds,
            waterfall.WriteCursorColour);

        waterfallColumnSequence = waterfall.ColumnSequence;
        waterfallWritePosition = waterfall.WritePosition;
        waterfallCursorBands = waterfall.Bands.ToArray();
        return waterfall.ColumnSequence;
    }

    private void RestorePreviousWaterfallCursor(
        IHost host,
        GOverlayWaterfallCommand waterfall)
    {
        if (waterfallWritePosition < 0
            || waterfallCursorBands.Length == 0)
            return;

        DrawWaterfallColumn(
            host,
            waterfall,
            waterfallWritePosition,
            waterfallCursorBands);
    }

    private static void DrawWaterfallColumn(
        IHost host,
        GOverlayWaterfallCommand waterfall,
        int writePosition,
        IReadOnlyList<float> bands)
    {
        var rowHeight =
            waterfall.Bounds.Height / GOverlayWaterfallGeometry.BandCount;
        var x = waterfall.Bounds.X
            + (writePosition
                * waterfall.ColumnWidth);
        for (var band = 0;
             band < GOverlayWaterfallGeometry.BandCount;
             band++)
        {
            var value = band < bands.Count
                ? bands[band]
                : 0;
            var visualRow =
                GOverlayWaterfallGeometry.BandCount - band - 1;
            Fill(
                host,
                new(
                    x,
                    waterfall.Bounds.Y + (visualRow * rowHeight),
                    waterfall.ColumnWidth,
                    rowHeight),
                waterfall.ColourFor(value));
        }
    }

    private void ClearSkippedWaterfallColumns(
        IHost host,
        GOverlayWaterfallCommand waterfall)
    {
        if (waterfallColumnSequence == long.MinValue
            || waterfallWritePosition < 0
            || waterfall.ColumnSequence <= waterfallColumnSequence + 1)
            return;

        var sequenceDelta =
            waterfall.ColumnSequence - waterfallColumnSequence;
        var background = waterfall.Background;
        if (sequenceDelta >= waterfall.ColumnCount)
        {
            Fill(host, waterfall.Bounds, background);
            return;
        }

        foreach (var position in
                 GOverlayWaterfallGeometry.SkippedPositions(
                     waterfallWritePosition,
                     sequenceDelta,
                     waterfall.ColumnCount))
        {
            Fill(
                host,
                new(
                    waterfall.Bounds.X
                        + (position
                            * waterfall.ColumnWidth),
                    waterfall.Bounds.Y,
                    waterfall.ColumnWidth,
                    waterfall.Bounds.Height),
                background);
        }
    }

    private static void Progress(
        IHost host,
        GOverlayProgressCommand progress)
    {
        Fill(host, progress.Bounds, progress.Background);
        var width = (int)Math.Round(progress.Bounds.Width * progress.Value);
        if (width > 0)
            Fill(
                host,
                new(
                    progress.Direction
                        == GOverlayProgressDirection.RightToLeft
                        ? progress.Bounds.Right - width
                        : progress.Bounds.X,
                    progress.Bounds.Y,
                    width,
                    progress.Bounds.Height),
                progress.Foreground);
    }

    private static void Fill(
        IHost host,
        GOverlayRectangle bounds,
        GOverlayColour colour) =>
        host.LCDSys2_Draw_Rectangle(
            bounds.X,
            bounds.Y,
            bounds.Right - 1,
            bounds.Bottom - 1,
            colour.Rgb565,
            0,
            0);

    private static void Line(IHost host, GOverlayLineCommand line)
    {
        var points = new ArrayList
        {
            new ArrayList { line.X1, line.Y1, line.Colour.Rgb565 },
            new ArrayList { line.X2, line.Y2, line.Colour.Rgb565 }
        };
        host.LCDSys2_Draw_Lines(points, true, line.Colour.Rgb565);
    }

    private static string FindFont(GOverlayDashboardScene scene)
    {
        _ = scene;
        return LegacyGOverlayPlugin.CurrentFontName;
    }

    private sealed class PreparedArtwork(
        GOverlayRectangle bounds,
        int[] colours)
    {
        public GOverlayRectangle Bounds { get; } = bounds;
        public int[] Colours { get; } = colours;
        public int NextPixel { get; set; }
    }
}
