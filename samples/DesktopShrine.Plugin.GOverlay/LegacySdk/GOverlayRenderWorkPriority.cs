using DesktopShrine.Plugin.GOverlay.Layout;

namespace DesktopShrine.Plugin.GOverlay.LegacySdk;

internal enum GOverlayRenderWorkPriority
{
    RecoveryOrGenerationChange = 0,
    PrimaryState = 100,
    ArtworkStage1 = 200,
    SecondaryState = 300,
    HardwareTelemetry = 400,
    Progress = 500,
    LiveVisual = 600,
    ArtworkStage2 = 700,
    ArtworkStage3 = 800,
    ArtworkStage4 = 900,
    ArtworkStage5 = 1000
}

internal static class GOverlayRenderWorkPolicy
{
    public static GOverlayRenderWorkPriority ForCommand(
        GOverlayDrawCommand command,
        GOverlayDashboardMode mode,
        bool fullRedraw)
    {
        if (fullRedraw)
            return GOverlayRenderWorkPriority.RecoveryOrGenerationChange;

        if (mode == GOverlayDashboardMode.Hardware)
        {
            if (command.Key.EndsWith(
                    ".name",
                    StringComparison.Ordinal)
                || command.Key.StartsWith(
                    "hardware.header.",
                    StringComparison.Ordinal)
                || command is GOverlayFillRectangleCommand
                    or GOverlayLineCommand
                    or GOverlayStrokeRectangleCommand)
                return GOverlayRenderWorkPriority.PrimaryState;

            return GOverlayRenderWorkPriority.HardwareTelemetry;
        }

        if (command.Key is "content.title"
            or "content.artist"
            or "content.context")
            return GOverlayRenderWorkPriority.PrimaryState;

        if (command.Key is "footer.elapsed" or "footer.progress")
            return GOverlayRenderWorkPriority.Progress;

        return command is GOverlayMeterBarCommand
            ? GOverlayRenderWorkPriority.LiveVisual
            : GOverlayRenderWorkPriority.SecondaryState;
    }

    public static GOverlayRenderWorkPriority ForArtworkStage(int stage) =>
        stage switch
        {
            <= 1 => GOverlayRenderWorkPriority.ArtworkStage1,
            2 => GOverlayRenderWorkPriority.ArtworkStage2,
            3 => GOverlayRenderWorkPriority.ArtworkStage3,
            4 => GOverlayRenderWorkPriority.ArtworkStage4,
            _ => GOverlayRenderWorkPriority.ArtworkStage5
        };
}
