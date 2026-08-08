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
    // This is the established Draw_Pixels batching used by the TN device. It
    // predates the removed renderer-wide pacing experiment.
    private const int ArtworkBatchesPerRender = 4;
    private readonly Dictionary<string, string> fingerprints =
        new(StringComparer.Ordinal);
    private Task<PreparedArtwork>? artworkPreparation;
    private PreparedArtwork? artworkTransfer;
    private string requestedArtworkFingerprint = string.Empty;
    private string displayedArtworkFingerprint = string.Empty;
    private string failedArtworkFingerprint = string.Empty;
    private bool requestedDontUseDrawPixels;
    private bool hasDisplayedArtwork;
    private long waterfallResetSequence = long.MinValue;
    private long waterfallColumnSequence = long.MinValue;
    private int waterfallWritePosition = -1;
    private float[] waterfallCursorBands = Array.Empty<float>();
    private bool fullRedrawPending = true;
    private string fullRedrawReason = "initial-state";
    private long connectionGeneration = long.MinValue;
    private bool? devicePresent;

    public void ResetForFullRedraw(
        string reason = "render-generation-change")
    {
        fingerprints.Clear();
        artworkPreparation = null;
        artworkTransfer = null;
        requestedArtworkFingerprint = string.Empty;
        displayedArtworkFingerprint = string.Empty;
        failedArtworkFingerprint = string.Empty;
        requestedDontUseDrawPixels = false;
        hasDisplayedArtwork = false;
        waterfallResetSequence = long.MinValue;
        waterfallColumnSequence = long.MinValue;
        waterfallWritePosition = -1;
        waterfallCursorBands = Array.Empty<float>();
        fullRedrawPending = true;
        fullRedrawReason = reason;
    }

    public long? Render(
        IHost host,
        GOverlayDashboardScene scene,
        GOverlayDashboardState state,
        int cacheRuns,
        Func<bool> isPriorityStateCurrent,
        GOverlayUsbConnectionSnapshot? connection = null)
    {
        // GOverlay resets cacheRuns to zero at its configured cache interval.
        // That is a hint to refresh cached values, not an indication that the
        // physical LCD was cleared. Clearing here caused a full-screen flash
        // and forced expensive artwork uploads on every reset.
        _ = cacheRuns;
        ObserveConnectionGeneration(host, connection);
        var now = DateTime.UtcNow;
        var pass = new GOverlaySdkRenderPass(
            host,
            GOverlayLcdCommandTrace.NextPassId());
        var diagnosticCommands = scene.Regions
            .SelectMany(region => region.Commands)
            .ToArray();
        var diagnosticArtwork = diagnosticCommands
            .OfType<GOverlayArtworkCommand>()
            .FirstOrDefault();
        var diagnosticWaterfall = diagnosticCommands
            .OfType<GOverlayWaterfallCommand>()
            .FirstOrDefault();
        var dirtyCommands = diagnosticCommands
            .Where(command => command is not GOverlayArtworkCommand
                && command is not GOverlayWaterfallCommand
                && IsDirty(command))
            .Select(command => new PrioritizedCommand(
                command,
                GOverlayRenderWorkPolicy.ForCommand(
                    command,
                    state.Mode,
                    fullRedrawPending)))
            .ToArray();
        var artworkPriority = diagnosticArtwork is not null
            && NeedsArtwork(diagnosticArtwork, state.DontUseDrawPixels)
                ? PendingArtworkPriority(
                    diagnosticArtwork,
                    state.DontUseDrawPixels)
                : (GOverlayRenderWorkPriority?)null;
        var waterfallDirty = diagnosticWaterfall is not null
            && NeedsWaterfall(diagnosticWaterfall);
        pass.Start(
            $"timestamp={now:O} insideDisplayOnLCD=true cacheRuns={cacheRuns} dashboardMode={state.Mode} dontUseDrawPixels={state.DontUseDrawPixels} revision={state.Revision} renderGeneration={state.RenderGeneration} fullRedraw={fullRedrawPending} fullRedrawReason={fullRedrawReason} sceneCommands={diagnosticCommands.Length} dirtyStatic={dirtyCommands.Length} artworkPresent={diagnosticArtwork?.HasArtwork == true} artworkDirty={artworkPriority.HasValue} artworkId={GOverlayLcdCommandTrace.ShortIdentifier(diagnosticArtwork?.ArtworkKey)} artworkSourceBytes={diagnosticArtwork?.ArtworkData.Length ?? 0} artworkPreparation={artworkPreparation?.Status.ToString() ?? "none"} artworkTransferPixel={artworkTransfer?.NextPixel ?? -1} artworkTransferStage={artworkTransfer?.NextRectangleStage ?? -1} artworkTransferStages={artworkTransfer?.RectanglePlan?.Stages.Count ?? -1} artworkTransferRectangles={artworkTransfer?.RectanglePlan?.TotalRectangleCommands ?? -1} audioDirty={waterfallDirty} audioHasColumn={diagnosticWaterfall?.HasColumn == true} audioResetSeq={diagnosticWaterfall?.ResetSequence ?? -1} audioColumnSeq={diagnosticWaterfall?.ColumnSequence ?? -1} audioWritePosition={diagnosticWaterfall?.WritePosition ?? -1}");
        host.DebugMessage(
            $"Desktop Shrine - render pass {pass.Id} begin DontUseDrawPixels={state.DontUseDrawPixels} thread={Environment.CurrentManagedThreadId} insideDisplayOnLCD=true");

        try
        {
            long? renderedWaterfall = diagnosticWaterfall?.HasColumn == true
                && diagnosticWaterfall.ColumnSequence
                    <= waterfallColumnSequence
                    ? diagnosticWaterfall.ColumnSequence
                    : null;
            var priorities = dirtyCommands
                .Select(item => item.Priority)
                .Concat(artworkPriority.HasValue
                    ? new[] { artworkPriority.Value }
                    : Array.Empty<GOverlayRenderWorkPriority>())
                .Concat(waterfallDirty
                    ? new[] { GOverlayRenderWorkPriority.LiveVisual }
                    : Array.Empty<GOverlayRenderWorkPriority>())
                .Distinct()
                .OrderBy(priority => (int)priority)
                .ToArray();

            foreach (var priority in priorities)
            {
                var commands = dirtyCommands
                    .Where(item => item.Priority == priority)
                    .Select(item => item.Command)
                    .ToArray();
                if (commands.Length > 0)
                {
                    LogWorkSelection(
                        host,
                        pass,
                        priority,
                        state,
                        $"commands={commands.Length}");
                    foreach (var command in commands)
                    {
                        using (pass.UseRegion(
                                   LogicalRegion(command, state.Mode)))
                            RenderChangedCommand(pass, command, scene);
                    }
                }

                if (artworkPriority == priority
                    && diagnosticArtwork is not null)
                {
                    LogWorkSelection(
                        host,
                        pass,
                        priority,
                        state,
                        $"generation={GOverlayLcdCommandTrace.ShortIdentifier(diagnosticArtwork.ArtworkKey)} stage={ArtworkStageNumber(priority)}");
                    using var artworkScope = pass.BeginRegion("Artwork");
                    _ = RenderArtwork(
                        pass,
                        host,
                        diagnosticArtwork,
                        scene,
                        isPriorityStateCurrent,
                        state.DontUseDrawPixels);
                }

                if (priority == GOverlayRenderWorkPriority.LiveVisual
                    && waterfallDirty
                    && diagnosticWaterfall is not null)
                {
                    var previousRenderedRevision = waterfallColumnSequence
                        == long.MinValue
                            ? "none"
                            : waterfallColumnSequence.ToString();
                    var coalesced = waterfallColumnSequence != long.MinValue
                        && diagnosticWaterfall.ColumnSequence
                            > waterfallColumnSequence + 1;
                    LogWorkSelection(
                        host,
                        pass,
                        priority,
                        state,
                        $"revision={diagnosticWaterfall.ColumnSequence} previousRenderedRevision={previousRenderedRevision} coalesced={coalesced.ToString().ToLowerInvariant()}");
                    using var waterfallScope = pass.BeginRegion("AudioMap");
                    renderedWaterfall = RenderWaterfall(
                        pass,
                        diagnosticWaterfall);
                }
            }
            fullRedrawPending = false;
            fullRedrawReason = string.Empty;
            pass.Complete("complete");
            return renderedWaterfall;
        }
        catch (GOverlayDeviceCommandException error)
        {
            pass.Complete("device-command-failed");
            ResetForFullRedraw("device-command-failed");
            host.DebugMessage(
                $"Desktop Shrine - device command failed operation={error.Operation} region={error.Region} error={error.GetBaseException().Message}");
            GOverlayLcdCommandTrace.Event(
                "SdkFailure",
                $"pass={pass.Id} operation={error.Operation} region={error.Region} exception={GOverlayLcdCommandTrace.Quote(error.ToString())}");
            return null;
        }
        catch (Exception error)
        {
            pass.Complete("renderer-failed");
            host.DebugMessage(
                $"Desktop Shrine - render pass {pass.Id} failed without being swallowed: {error}");
            throw;
        }
    }

    private bool NeedsArtwork(
        GOverlayArtworkCommand artwork,
        bool dontUseDrawPixels)
    {
        return artwork.Fingerprint != requestedArtworkFingerprint
            || dontUseDrawPixels != requestedDontUseDrawPixels
            || (artwork.HasArtwork
                && artwork.Fingerprint != displayedArtworkFingerprint
                && artwork.Fingerprint != failedArtworkFingerprint);
    }

    private bool IsDirty(GOverlayDrawCommand command) =>
        !fingerprints.TryGetValue(command.Key, out var fingerprint)
        || fingerprint != command.Fingerprint;

    private bool NeedsWaterfall(GOverlayWaterfallCommand waterfall) =>
        waterfall.ResetSequence != waterfallResetSequence
        || (waterfall.HasColumn
            && waterfall.ColumnSequence > waterfallColumnSequence);

    private GOverlayRenderWorkPriority PendingArtworkPriority(
        GOverlayArtworkCommand artwork,
        bool dontUseDrawPixels)
    {
        if (artwork.Fingerprint != requestedArtworkFingerprint
            || dontUseDrawPixels != requestedDontUseDrawPixels
            || artworkTransfer?.RectanglePlan is null)
            return GOverlayRenderWorkPriority.ArtworkStage1;

        var plan = artworkTransfer.RectanglePlan;
        var stageIndex = artworkTransfer.NextRectangleStage;
        while (stageIndex < plan.Stages.Count
               && plan.Stages[stageIndex].Rectangles.Count == 0)
            stageIndex++;

        return GOverlayRenderWorkPolicy.ForArtworkStage(
            Math.Min(stageIndex + 1, plan.Stages.Count));
    }

    private void ObserveConnectionGeneration(
        IHost host,
        GOverlayUsbConnectionSnapshot? connection)
    {
        if (connection is null)
            return;

        if (connectionGeneration == long.MinValue)
        {
            connectionGeneration = connection.Generation;
            devicePresent = connection.IsPresent;
            return;
        }
        if (connection.Generation == connectionGeneration)
            return;

        var previousPresence = devicePresent;
        connectionGeneration = connection.Generation;
        devicePresent = connection.IsPresent;
        ResetForFullRedraw("device-generation-change");
        var details =
            $"generation={connection.Generation} present={connection.IsPresent} previousPresent={previousPresence} changedAt={connection.ChangedAtUtc:O}";
        host.DebugMessage(
            "Desktop Shrine - renderer device generation changed; "
            + "pending work discarded; "
            + details);
        GOverlayLcdCommandTrace.Event(
            "RendererDeviceGenerationChanged",
            details);
    }

    private static int ArtworkStageNumber(
        GOverlayRenderWorkPriority priority) =>
        priority switch
        {
            GOverlayRenderWorkPriority.ArtworkStage2 => 2,
            GOverlayRenderWorkPriority.ArtworkStage3 => 3,
            GOverlayRenderWorkPriority.ArtworkStage4 => 4,
            GOverlayRenderWorkPriority.ArtworkStage5 => 5,
            _ => 1
        };

    private static void LogWorkSelection(
        IHost host,
        GOverlaySdkRenderPass pass,
        GOverlayRenderWorkPriority priority,
        GOverlayDashboardState state,
        string details)
    {
        var message =
            $"selected={priority} priority={(int)priority} renderGeneration={state.RenderGeneration} revision={state.Revision} {details}";
        host.DebugMessage("Desktop Shrine - RENDER WORK " + message);
        GOverlayLcdCommandTrace.Event(
            "RenderWork",
            $"pass={pass.Id} {message}");
    }

    private static string LogicalRegion(
        GOverlayDrawCommand command,
        GOverlayDashboardMode mode)
    {
        if (command is GOverlayProgressCommand)
            return mode == GOverlayDashboardMode.Hardware
                ? "Hardware"
                : "Progress";
        if (command is GOverlayMeterBarCommand)
            return mode == GOverlayDashboardMode.Hardware
                ? "Hardware"
                : "Other";
        if (command is GOverlayTextCommand)
            return mode == GOverlayDashboardMode.Hardware
                ? "Hardware"
                : "MediaText";
        if (command is GOverlayFillRectangleCommand
            or GOverlayStrokeRectangleCommand
            or GOverlayLineCommand)
            return "Background/Layout";
        return "Other";
    }

    private void RenderChangedCommand(
        GOverlaySdkRenderPass pass,
        GOverlayDrawCommand command,
        GOverlayDashboardScene scene)
    {
        if (fingerprints.TryGetValue(
                command.Key,
                out var previous)
            && previous == command.Fingerprint)
        {
            return;
        }

        RenderCommand(pass, command, scene);
        fingerprints[command.Key] = command.Fingerprint;
    }

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
        bool dontUseDrawPixels)
    {
        if (artwork.Fingerprint != requestedArtworkFingerprint
            || dontUseDrawPixels != requestedDontUseDrawPixels)
        {
            if (artworkTransfer?.RectanglePlan is not null
                && artworkTransfer.NextRectangleStage
                    < artworkTransfer.RectanglePlan.Stages.Count)
            {
                host.DebugMessage(
                    $"Desktop Shrine - abandoned artwork refinement artworkId={artworkTransfer.ArtworkId} completedStages={artworkTransfer.NextRectangleStage}/{artworkTransfer.RectanglePlan.Stages.Count}");
            }
            requestedArtworkFingerprint = artwork.Fingerprint;
            requestedDontUseDrawPixels = dontUseDrawPixels;
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

            artworkPreparation = Task.Run(
                () => PrepareArtwork(artwork, dontUseDrawPixels));
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
                    Task.Run(() => PrepareArtwork(
                        artwork,
                        dontUseDrawPixels));
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
                Placeholder(pass, artwork, scene);
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
            Fill(pass, artwork.Bounds, artwork.Background);
            if (dontUseDrawPixels)
            {
                var plan = artworkTransfer.RectanglePlan
                    ?? throw new InvalidOperationException(
                        "Prepared adaptive artwork has no rectangle plan.");
                var stage5 = plan.Stages[
                    AdaptiveArtworkRectangleCompressor.DefaultStageCount - 1];
                var summary =
                    $"ARTWORK RECTCOMP PLAN artworkId={artworkTransfer.ArtworkId} dontUseDrawPixels=true source={artworkTransfer.SourceWidth}x{artworkTransfer.SourceHeight} destination={artworkTransfer.Bounds.Width}x{artworkTransfer.Bounds.Height} stages={plan.Stages.Count} stageRectangles={string.Join(",", plan.Stages.Select(stage => stage.Rectangles.Count))} stageTotals={string.Join(",", plan.Stages.Select(stage => stage.CumulativeLeafCount))} stageTargets={string.Join(",", plan.Stages.Take(AdaptiveArtworkRectangleCompressor.DefaultNormalStageCount).Select(stage => stage.RectangleTarget))} stage5Eligible={plan.Stage5Eligible.ToString().ToLowerInvariant()} stage5Rectangles={stage5.Rectangles.Count} stage5Budget={plan.Stage5MaximumRectangles} stage5ErrorPerPixelThreshold={plan.Stage5ErrorPerPixelThreshold} remainingErrorAfterStage4={plan.RemainingErrorAfterStage4} remainingErrorAfterStage5={plan.RemainingErrorAfterStage5} minBlock={plan.MinimumBlockDimension} minBlockStage5={plan.Stage5MinimumBlockDimension} paletteColours={plan.PaletteColourCount} prepareMs={artworkTransfer.PrepareMilliseconds}";
                host.DebugMessage("Desktop Shrine - " + summary);
                GOverlayLcdCommandTrace.Event(
                    "ArtworkRectangleCompression",
                    $"pass={pass.Id} {summary}");
            }
            else
            {
                host.DebugMessage(
                    $"Desktop Shrine - render pass {pass.Id} transferring {artworkTransfer.Bounds.Width}x{artworkTransfer.Bounds.Height} opaque artwork into {artwork.Bounds.Width}x{artwork.Bounds.Height} sourceBytes={artwork.ArtworkData.Length} preparedRgb565Bytes={artworkTransfer.Colours.Length * 2}");
            }
        }

        if (dontUseDrawPixels)
        {
            var plan = artworkTransfer.RectanglePlan
                ?? throw new InvalidOperationException(
                    "Prepared adaptive artwork has no rectangle plan.");
            SkipEmptyArtworkStages(host, artworkTransfer, plan);

            if (artworkTransfer.NextRectangleStage < plan.Stages.Count)
            {
                var stage = plan.Stages[artworkTransfer.NextRectangleStage];
                foreach (var rectangle in stage.Rectangles)
                {
                    if (!isPriorityStateCurrent())
                        return CancelArtworkTransfer(host);
                    pass.Rectangle(
                        new GOverlayRectangle(
                            artworkTransfer.Bounds.X + rectangle.X,
                            artworkTransfer.Bounds.Y + rectangle.Y,
                            rectangle.Width,
                            rectangle.Height),
                        ColourFromRgb565(rectangle.Colour));
                    if (!isPriorityStateCurrent())
                        return CancelArtworkTransfer(host);
                }

                LogArtworkStageRendered(
                    host,
                    artworkTransfer,
                    stage,
                    plan.Stages.Count);
                artworkTransfer.NextRectangleStage++;
                SkipEmptyArtworkStages(host, artworkTransfer, plan);
            }

            if (artworkTransfer.NextRectangleStage < plan.Stages.Count)
                return true;

            displayedArtworkFingerprint = artwork.Fingerprint;
            hasDisplayedArtwork = true;
            artworkTransfer = null;
            host.DebugMessage(
                "Desktop Shrine - artwork rectangle transfer complete");
            return false;
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
            var batchStart = artworkTransfer.NextPixel;
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
            pass.Pixels(
                pixels,
                $"component=Artwork artworkId={GOverlayLcdCommandTrace.ShortIdentifier(artwork.ArtworkKey)} contentType={GOverlayLcdCommandTrace.Quote(artwork.ContentType, 64)} sourceWidth={artworkTransfer.SourceWidth} sourceHeight={artworkTransfer.SourceHeight} sourceBytes={artwork.ArtworkData.Length} destinationX={artworkTransfer.Bounds.X} destinationY={artworkTransfer.Bounds.Y} destinationWidth={artworkTransfer.Bounds.Width} destinationHeight={artworkTransfer.Bounds.Height} targetWidth={artwork.Bounds.Width} targetHeight={artwork.Bounds.Height} pixelFormat=RGB565 opaque=true preparedRgb565Bytes={artworkTransfer.Colours.Length * 2L} batchIndex={batches} batchStartPixel={batchStart}");
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

    private static void LogArtworkStageRendered(
        IHost host,
        PreparedArtwork artwork,
        ArtworkRectangleStage stage,
        int stageCount)
    {
        if (!string.IsNullOrEmpty(stage.SkipReason))
        {
            host.DebugMessage(
                $"Desktop Shrine - artwork stage {stage.Number} skipped reason={stage.SkipReason} artworkId={artwork.ArtworkId}");
            return;
        }
        host.DebugMessage(
            $"Desktop Shrine - artwork stage {stage.Number}/{stageCount} rendered rectangles={stage.Rectangles.Count} artworkId={artwork.ArtworkId}");
    }

    private static void SkipEmptyArtworkStages(
        IHost host,
        PreparedArtwork artwork,
        ArtworkRectanglePlan plan)
    {
        while (artwork.NextRectangleStage < plan.Stages.Count
               && plan.Stages[artwork.NextRectangleStage]
                   .Rectangles.Count == 0)
        {
            LogArtworkStageRendered(
                host,
                artwork,
                plan.Stages[artwork.NextRectangleStage],
                plan.Stages.Count);
            artwork.NextRectangleStage++;
        }
    }

    /*
     * The adaptive branch above intentionally advances one prepared, non-empty
     * refinement stage per DisplayOnLCD invocation. It is image refinement,
     * not a renderer command/time budget; all unrelated regions continue to
     * render normally in the same callback.
     */

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
        GOverlayArtworkCommand artwork,
        bool dontUseDrawPixels)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
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

        ArtworkRectanglePlan? rectanglePlan = null;
        if (dontUseDrawPixels)
        {
            rectanglePlan =
                AdaptiveArtworkRectangleCompressor.CreateProgressivePlan(
                colours,
                scaled.Width,
                scaled.Height);
        }

        stopwatch.Stop();
        return new(
            imageBounds,
            colours,
            source.Width,
            source.Height,
            GOverlayLcdCommandTrace.ShortIdentifier(artwork.ArtworkKey),
            rectanglePlan,
            stopwatch.ElapsedMilliseconds);
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
        GOverlayWaterfallCommand waterfall)
    {
        GOverlayLcdCommandTrace.Event(
            "AudioMapFrame",
            $"pass={pass.Id} thread={Environment.CurrentManagedThreadId} primitive=LCDSys2_Draw_Rectangle resetSequence={waterfall.ResetSequence} columnSequence={waterfall.ColumnSequence} writePosition={waterfall.WritePosition} columnWidth={waterfall.ColumnWidth} columnCount={waterfall.ColumnCount} bandCount={waterfall.Bands.Count} hasColumn={waterfall.HasColumn} boundsX={waterfall.Bounds.X} boundsY={waterfall.Bounds.Y} boundsWidth={waterfall.Bounds.Width} boundsHeight={waterfall.Bounds.Height}");

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
        ClearSkippedWaterfallColumns(pass, waterfall);
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
            Fill(pass, waterfall.Bounds, background);
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

    private static GOverlayColour ColourFromRgb565(int value) => new(
        (byte)(((value >> 11) & 0x1f) * 255 / 31),
        (byte)(((value >> 5) & 0x3f) * 255 / 63),
        (byte)((value & 0x1f) * 255 / 31));

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
        int[] colours,
        int sourceWidth,
        int sourceHeight,
        string artworkId,
        ArtworkRectanglePlan? rectanglePlan,
        long prepareMilliseconds)
    {
        public GOverlayRectangle Bounds { get; } = bounds;
        public int[] Colours { get; } = colours;
        public int SourceWidth { get; } = sourceWidth;
        public int SourceHeight { get; } = sourceHeight;
        public string ArtworkId { get; } = artworkId;
        public ArtworkRectanglePlan? RectanglePlan { get; } = rectanglePlan;
        public long PrepareMilliseconds { get; } = prepareMilliseconds;
        public int NextPixel { get; set; }
        public int NextRectangleStage { get; set; }
    }

    private sealed class PrioritizedCommand(
        GOverlayDrawCommand command,
        GOverlayRenderWorkPriority priority)
    {
        public GOverlayDrawCommand Command { get; } = command;
        public GOverlayRenderWorkPriority Priority { get; } = priority;
    }
}
