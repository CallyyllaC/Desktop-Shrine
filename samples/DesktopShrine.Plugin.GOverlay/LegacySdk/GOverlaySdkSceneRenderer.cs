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
    private long nextPassId;
    private DateTime lastPassCompletedUtc;
    private DateTime unavailableUntilUtc;
    private bool recovering;
    private bool previousCompatibilityPassUsedArtwork;
    private GOverlayRenderCompatibilityMode? activeCompatibilityMode;
    private long connectionGeneration = -1;
    private bool? devicePresent;

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
        GOverlayDashboardState state,
        GOverlayUsbConnectionSnapshot connection,
        int cacheRuns,
        Func<bool> isPriorityStateCurrent)
    {
        // GOverlay resets cacheRuns to zero at its configured cache interval.
        // That is a hint to refresh cached values, not an indication that the
        // physical LCD was cleared. Clearing here caused a full-screen flash
        // and forced expensive artwork uploads on every reset.
        _ = cacheRuns;
        var now = DateTime.UtcNow;
        var compatibilityMode = state.RenderCompatibilityMode
            == GOverlayRenderCompatibilityMode.IpsSafe;
        if (activeCompatibilityMode != state.RenderCompatibilityMode)
        {
            activeCompatibilityMode = state.RenderCompatibilityMode;
            ResetForFullRedraw();
            host.DebugMessage(
                $"Desktop Shrine - renderer compatibility={state.RenderCompatibilityMode} firmware={state.DeviceFirmwareRevision}; Interfaces.dll exposes no firmware API and USB REV is not treated as firmware");
        }

        if (connectionGeneration < 0)
        {
            connectionGeneration = connection.Generation;
            devicePresent = connection.IsPresent;
        }
        else if (connection.Generation != connectionGeneration)
        {
            var previousPresence = devicePresent;
            connectionGeneration = connection.Generation;
            devicePresent = connection.IsPresent;
            ResetForFullRedraw();
            recovering = true;
            if (connection.IsPresent == true)
            {
                unavailableUntilUtc = now.AddMilliseconds(
                    state.ReconnectStabilizationMilliseconds);
                host.DebugMessage(
                    $"Desktop Shrine - device-state=reconnected source=usb-monitor generation={connection.Generation} changedAt={connection.ChangedAtUtc:O}; pending physical work discarded; recoveryAfter={unavailableUntilUtc:O}");
            }
            else
            {
                unavailableUntilUtc = DateTime.MaxValue;
                host.DebugMessage(
                    $"Desktop Shrine - device-state=disconnected source=usb-monitor generation={connection.Generation} changedAt={connection.ChangedAtUtc:O} previousPresent={previousPresence}; pending physical work discarded");
            }
        }

        var callbackGap = lastPassCompletedUtc == default
            ? TimeSpan.Zero
            : now - lastPassCompletedUtc;
        var gapThreshold = TimeSpan.FromMilliseconds(Math.Max(
            2000,
            state.ReconnectStabilizationMilliseconds * 2));
        if (compatibilityMode
            && lastPassCompletedUtc != default
            && callbackGap >= gapThreshold
            && unavailableUntilUtc == default)
        {
            ResetForFullRedraw();
            recovering = true;
            unavailableUntilUtc = now.AddMilliseconds(
                state.ReconnectStabilizationMilliseconds);
            host.DebugMessage(
                $"Desktop Shrine - device-state=disconnected inferredFrom=callback-gap gapMs={(long)callbackGap.TotalMilliseconds}; pending physical work discarded; recoveryAfter={unavailableUntilUtc:O}");
        }

        var pass = new GOverlaySdkRenderPass(
            host,
            Interlocked.Increment(ref nextPassId),
            compatibilityMode,
            state.MaximumCommandsPerRefresh,
            state.MaximumDrawMilliseconds);
        host.DebugMessage(
            $"Desktop Shrine - render pass {pass.Id} begin mode={state.RenderCompatibilityMode} firmware={state.DeviceFirmwareRevision} thread={Environment.CurrentManagedThreadId} insideDisplayOnLCD=true recovering={recovering}");

        if (unavailableUntilUtc != default && now < unavailableUntilUtc)
        {
            host.DebugMessage(
                $"Desktop Shrine - render pass {pass.Id} device-state=stabilizing; physical draw skipped until {unavailableUntilUtc:O}");
            pass.Complete("stabilizing");
            lastPassCompletedUtc = DateTime.UtcNow;
            return null;
        }
        if (unavailableUntilUtc != default)
        {
            unavailableUntilUtc = default;
            host.DebugMessage(
                $"Desktop Shrine - render pass {pass.Id} device-state=reconnected; starting controlled staged redraw with audio suppressed");
        }

        try
        {
            GOverlayArtworkCommand? artwork = null;
            GOverlayWaterfallCommand? waterfall = null;
            var staticChanged = false;
            var staticDeferred = false;
            foreach (var region in scene.Regions)
            {
                using var regionScope = pass.BeginRegion(region.Id);
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

                    if (!RenderChangedCommand(
                            pass,
                            command,
                            scene,
                            out var changed))
                        staticDeferred = true;
                    staticChanged |= changed;
                }
            }

            var artworkPending = artwork is not null && NeedsArtwork(artwork);
            var artworkCommands = 0;
            if (artwork is not null && artworkPending)
            {
                var alternateWithAudio = compatibilityMode
                    && !recovering
                    && previousCompatibilityPassUsedArtwork
                    && waterfall?.HasColumn == true;
                if (staticChanged || staticDeferred || alternateWithAudio)
                {
                    var reason = alternateWithAudio
                        ? "alternating expensive artwork with latest audio"
                        : "static/full redraw has priority";
                    host.DebugMessage(
                        $"Desktop Shrine - render pass {pass.Id} region=artwork deferred reason={reason}");
                }
                else
                {
                    using var artworkScope = pass.BeginRegion("artwork");
                    var before = pass.CommandCount;
                    artworkPending = RenderArtwork(
                        pass,
                        host,
                        artwork,
                        scene,
                        isPriorityStateCurrent,
                        state.MaximumArtworkBatchesPerRefresh);
                    artworkCommands = pass.CommandCount - before;
                }
            }

            var suppressAudioReason = string.Empty;
            if (compatibilityMode)
            {
                if (recovering)
                    suppressAudioReason = "controlled base-screen recovery";
                else if (staticChanged || staticDeferred)
                    suppressAudioReason = "static/full redraw in this refresh";
                else if (artworkCommands > 0)
                    suppressAudioReason = "artwork transfer in this refresh";
                else if (pass.BudgetExhausted)
                    suppressAudioReason = "command/time budget exhausted";
                else if (pass.Id % Math.Max(1, state.AudioRefreshDivisor) != 0)
                    suppressAudioReason = "configured audio refresh divisor";
            }

            long? renderedWaterfall = null;
            if (waterfall is not null && string.IsNullOrEmpty(suppressAudioReason))
            {
                using var waterfallScope = pass.BeginRegion("audio-map");
                renderedWaterfall = RenderWaterfall(
                    pass,
                    waterfall,
                    compatibilityMode);
                if (waterfall.HasColumn && !renderedWaterfall.HasValue)
                    suppressAudioReason = "command/time budget deferred audio atomically";
            }
            if (waterfall?.HasColumn == true
                && !string.IsNullOrEmpty(suppressAudioReason))
                host.DebugMessage(
                    $"Desktop Shrine - render pass {pass.Id} audio skipped/deferred reason={suppressAudioReason}; newest frame retained, stale frames coalesced");

            if (recovering && !staticDeferred && !artworkPending)
            {
                recovering = false;
                host.DebugMessage(
                    $"Desktop Shrine - render pass {pass.Id} device-state=stable base-screen-restored; audio resumes on a later refresh");
            }

            previousCompatibilityPassUsedArtwork = artworkCommands > 0;
            pass.Complete(pass.BudgetExhausted ? "budget-deferred" : "complete");
            lastPassCompletedUtc = DateTime.UtcNow;
            return renderedWaterfall;
        }
        catch (GOverlayDeviceCommandException error)
        {
            pass.Complete("device-command-failed");
            ResetForFullRedraw();
            recovering = true;
            unavailableUntilUtc = DateTime.UtcNow.AddMilliseconds(
                state.ReconnectStabilizationMilliseconds);
            lastPassCompletedUtc = DateTime.UtcNow;
            host.DebugMessage(
                $"Desktop Shrine - device-state=disconnected operation={error.Operation} region={error.Region} error={error.GetBaseException().Message}; pending physical work discarded; recoveryAfter={unavailableUntilUtc:O}");
            return null;
        }
        catch (Exception error)
        {
            pass.Complete("renderer-failed");
            lastPassCompletedUtc = DateTime.UtcNow;
            host.DebugMessage(
                $"Desktop Shrine - render pass {pass.Id} failed without being swallowed: {error}");
            throw;
        }
    }

    private bool NeedsArtwork(GOverlayArtworkCommand artwork)
    {
        return artwork.Fingerprint != requestedArtworkFingerprint
            || (artwork.HasArtwork
                && artwork.Fingerprint != displayedArtworkFingerprint
                && artwork.Fingerprint != failedArtworkFingerprint);
    }

    private bool RenderChangedCommand(
        GOverlaySdkRenderPass pass,
        GOverlayDrawCommand command,
        GOverlayDashboardScene scene,
        out bool changed)
    {
        if (fingerprints.TryGetValue(
                command.Key,
                out var previous)
            && previous == command.Fingerprint)
        {
            changed = false;
            return true;
        }

        changed = false;
        if (!pass.CanIssue(EstimatedCommandCount(command)))
            return false;

        RenderCommand(pass, command, scene);
        fingerprints[command.Key] = command.Fingerprint;
        changed = true;
        return true;
    }

    private static int EstimatedCommandCount(GOverlayDrawCommand command) =>
        command is GOverlayMeterBarCommand or GOverlayProgressCommand ? 2 : 1;

    private static void RenderCommand(
        GOverlaySdkRenderPass pass,
        GOverlayDrawCommand command,
        GOverlayDashboardScene scene)
    {
        switch (command)
        {
            case GOverlayFillRectangleCommand fill:
                Fill(pass, fill.Bounds, fill.Colour);
                break;
            case GOverlayStrokeRectangleCommand stroke:
                pass.Rectangle(
                    stroke.Bounds,
                    stroke.Colour,
                    stroke.Thickness);
                break;
            case GOverlayLineCommand line:
                Line(pass, line);
                break;
            case GOverlayTextCommand text:
                Text(pass, text, scene);
                break;
            case GOverlayMeterBarCommand meter:
                Meter(pass, meter);
                break;
            case GOverlayProgressCommand progress:
                Progress(pass, progress);
                break;
        }
    }

    private static void Text(
        GOverlaySdkRenderPass pass,
        GOverlayTextCommand text,
        GOverlayDashboardScene scene)
    {
        var font = FindFont(scene);
        pass.Text(text, font);
    }

    private bool RenderArtwork(
        GOverlaySdkRenderPass pass,
        IHost host,
        GOverlayArtworkCommand artwork,
        GOverlayDashboardScene scene,
        Func<bool> isPriorityStateCurrent,
        int maximumArtworkBatches)
    {
        if (artwork.Fingerprint != requestedArtworkFingerprint)
        {
            if ((!artwork.HasArtwork || !hasDisplayedArtwork)
                && !pass.CanIssue(4))
                return true;

            requestedArtworkFingerprint = artwork.Fingerprint;
            failedArtworkFingerprint = string.Empty;
            artworkPreparation = null;
            artworkTransfer = null;

            if (!artwork.HasArtwork)
            {
                Placeholder(pass, artwork, scene);
                displayedArtworkFingerprint = artwork.Fingerprint;
                hasDisplayedArtwork = false;
                return false;
            }

            // Keep the previous image visible while the replacement is decoded
            // and resized. On the first image, establish a useful placeholder.
            if (!hasDisplayedArtwork)
                Placeholder(pass, artwork, scene);

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
                if (!pass.CanIssue(4))
                    return true;
                var error = artworkPreparation.Exception!
                    .GetBaseException()
                    .Message;
                host.DebugMessage(
                    "Desktop Shrine - artwork preparation failed: " + error);
                failedArtworkFingerprint = artwork.Fingerprint;
                artworkPreparation = null;
                Placeholder(pass, artwork, scene);
                displayedArtworkFingerprint = artwork.Fingerprint;
                hasDisplayedArtwork = false;
                return false;
            }

            if (!isPriorityStateCurrent())
                return CancelArtworkTransfer(host);

            if (!pass.CanIssue(2))
                return true;

            artworkTransfer = artworkPreparation.Result;
            artworkPreparation = null;
            // A cancelled upload can leave pixels from several tracks in this
            // region. One cheap rectangle reset gives the replacement a clean
            // canvas without touching metadata, footer or waterfall history.
            Fill(pass, artwork.Bounds, artwork.Background);
            host.DebugMessage(
                $"Desktop Shrine - render pass {pass.Id} transferring {artworkTransfer.Bounds.Width}x{artworkTransfer.Bounds.Height} opaque artwork into {artwork.Bounds.Width}x{artwork.Bounds.Height} sourceBytes={artwork.ArtworkData.Length} preparedRgb565Bytes={artworkTransfer.Colours.Length * 2}");
        }

        // Transfer a bounded slice at native GOverlay packet size. The next
        // display callback resumes from NextPixel, while newer state can cancel
        // the transfer between any two packets.
        var batches = 0;
        while (artworkTransfer.NextPixel < artworkTransfer.Colours.Length
               && batches < Math.Max(1, maximumArtworkBatches)
               && pass.CanIssue(1))
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
            pass.Pixels(pixels);
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
        GOverlaySdkRenderPass pass,
        GOverlayArtworkCommand artwork,
        GOverlayDashboardScene scene)
    {
        Fill(pass, artwork.Bounds, artwork.Background);
        Line(
            pass,
            new(
                "placeholder.one",
                artwork.Bounds.X,
                artwork.Bounds.Y,
                artwork.Bounds.Right - 1,
                artwork.Bounds.Bottom - 1,
                artwork.Foreground));
        Line(
            pass,
            new(
                "placeholder.two",
                artwork.Bounds.Right - 1,
                artwork.Bounds.Y,
                artwork.Bounds.X,
                artwork.Bounds.Bottom - 1,
                artwork.Foreground));
        Text(
            pass,
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
        GOverlaySdkRenderPass pass,
        GOverlayMeterBarCommand meter)
    {
        Fill(pass, meter.Bounds, meter.Background);
        var height = Math.Max(
            1,
            (int)Math.Round(meter.Bounds.Height * meter.Value));
        Fill(
            pass,
            new(
                meter.Bounds.X,
                meter.Bounds.Bottom - height,
                meter.Bounds.Width,
                height),
            meter.Foreground);
    }

    private long? RenderWaterfall(
        GOverlaySdkRenderPass pass,
        GOverlayWaterfallCommand waterfall,
        bool compatibilityMode)
    {
        var estimatedCommands = EstimateWaterfallCommands(
            waterfall,
            compatibilityMode);
        if (!pass.CanIssue(estimatedCommands))
            return null;

        if (waterfall.ResetSequence != waterfallResetSequence)
        {
            Fill(
                pass,
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

        RestorePreviousWaterfallCursor(pass, waterfall);
        ClearSkippedWaterfallColumns(pass, waterfall, compatibilityMode);
        DrawWaterfallColumn(
            pass,
            waterfall,
            waterfall.WritePosition,
            waterfall.Bands);
        Fill(
            pass,
            waterfall.WriteCursorBounds,
            waterfall.WriteCursorColour);

        waterfallColumnSequence = waterfall.ColumnSequence;
        waterfallWritePosition = waterfall.WritePosition;
        waterfallCursorBands = waterfall.Bands.ToArray();
        return waterfall.ColumnSequence;
    }

    private void RestorePreviousWaterfallCursor(
        GOverlaySdkRenderPass pass,
        GOverlayWaterfallCommand waterfall)
    {
        if (waterfallWritePosition < 0
            || waterfallCursorBands.Length == 0)
            return;

        DrawWaterfallColumn(
            pass,
            waterfall,
            waterfallWritePosition,
            waterfallCursorBands);
    }

    private static void DrawWaterfallColumn(
        GOverlaySdkRenderPass pass,
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
                pass,
                new(
                    x,
                    waterfall.Bounds.Y + (visualRow * rowHeight),
                    waterfall.ColumnWidth,
                    rowHeight),
                waterfall.ColourFor(value));
        }
    }

    private void ClearSkippedWaterfallColumns(
        GOverlaySdkRenderPass pass,
        GOverlayWaterfallCommand waterfall,
        bool compatibilityMode)
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
            Fill(pass, waterfall.Bounds, background);
            return;
        }

        if (compatibilityMode)
        {
            foreach (var range in SkippedRanges(waterfall, sequenceDelta))
                Fill(
                    pass,
                    new(
                        waterfall.Bounds.X
                            + (range.Start * waterfall.ColumnWidth),
                        waterfall.Bounds.Y,
                        range.Count * waterfall.ColumnWidth,
                        waterfall.Bounds.Height),
                    background);
            return;
        }

        foreach (var position in GOverlayWaterfallGeometry.SkippedPositions(
                     waterfallWritePosition,
                     sequenceDelta,
                     waterfall.ColumnCount))
            Fill(
                pass,
                new(
                    waterfall.Bounds.X + (position * waterfall.ColumnWidth),
                    waterfall.Bounds.Y,
                    waterfall.ColumnWidth,
                    waterfall.Bounds.Height),
                background);
    }

    private int EstimateWaterfallCommands(
        GOverlayWaterfallCommand waterfall,
        bool compatibilityMode)
    {
        if (waterfall.ResetSequence != waterfallResetSequence)
        {
            // Resetting discards the previous cursor and sequence before the
            // new column is drawn, so old-history restoration/skipped-column
            // work must not be included in this atomic estimate.
            return 1 + (waterfall.HasColumn
                ? GOverlayWaterfallGeometry.BandCount + 1
                : 0);
        }

        var count = 0;
        if (!waterfall.HasColumn
            || waterfall.ColumnSequence <= waterfallColumnSequence)
            return count;

        if (waterfallWritePosition >= 0 && waterfallCursorBands.Length > 0)
            count += GOverlayWaterfallGeometry.BandCount;

        if (waterfallColumnSequence != long.MinValue
            && waterfall.ColumnSequence > waterfallColumnSequence + 1)
        {
            var delta = waterfall.ColumnSequence - waterfallColumnSequence;
            if (delta >= waterfall.ColumnCount)
                count++;
            else if (compatibilityMode)
                count += SkippedRanges(waterfall, delta).Count;
            else
                count += GOverlayWaterfallGeometry.SkippedPositions(
                    waterfallWritePosition,
                    delta,
                    waterfall.ColumnCount).Count();
        }

        return count + GOverlayWaterfallGeometry.BandCount + 1;
    }

    private IReadOnlyList<(int Start, int Count)> SkippedRanges(
        GOverlayWaterfallCommand waterfall,
        long sequenceDelta)
    {
        var positions = GOverlayWaterfallGeometry.SkippedPositions(
                waterfallWritePosition,
                sequenceDelta,
                waterfall.ColumnCount)
            .ToArray();
        if (positions.Length == 0)
            return Array.Empty<(int, int)>();

        var ranges = new List<(int Start, int Count)>();
        var start = positions[0];
        var count = 1;
        for (var index = 1; index < positions.Length; index++)
        {
            if (positions[index] == positions[index - 1] + 1)
            {
                count++;
                continue;
            }
            ranges.Add((start, count));
            start = positions[index];
            count = 1;
        }
        ranges.Add((start, count));
        return ranges;
    }

    private static void Progress(
        GOverlaySdkRenderPass pass,
        GOverlayProgressCommand progress)
    {
        Fill(pass, progress.Bounds, progress.Background);
        var width = (int)Math.Round(progress.Bounds.Width * progress.Value);
        if (width > 0)
            Fill(
                pass,
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
        GOverlaySdkRenderPass pass,
        GOverlayRectangle bounds,
        GOverlayColour colour) =>
        pass.Rectangle(bounds, colour);

    private static void Line(
        GOverlaySdkRenderPass pass,
        GOverlayLineCommand line)
    {
        var points = new ArrayList
        {
            new ArrayList { line.X1, line.Y1, line.Colour.Rgb565 },
            new ArrayList { line.X2, line.Y2, line.Colour.Rgb565 }
        };
        pass.Lines(points, line.Colour.Rgb565);
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
