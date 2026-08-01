using System.Globalization;
using System.Security;
using System.Text;

namespace DesktopShrine.Plugin.GOverlay.Layout;

public static class GOverlaySvgRenderer
{
    public static string Render(GOverlayDashboardScene scene)
    {
        var output = new StringBuilder();
        output.AppendLine(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"480\" height=\"320\" viewBox=\"0 0 480 320\">");
        output.AppendLine(
            "<style>text{font-family:'Segoe UI',sans-serif} .label{letter-spacing:.7px}</style>");

        foreach (var region in scene.Regions)
            foreach (var command in region.Commands)
                RenderCommand(output, command);

        output.AppendLine("</svg>");
        return output.ToString();
    }

    private static void RenderCommand(
        StringBuilder output,
        GOverlayDrawCommand command)
    {
        switch (command)
        {
            case GOverlayFillRectangleCommand fill:
                Rect(output, fill.Bounds, fill.Colour);
                break;
            case GOverlayStrokeRectangleCommand stroke:
                output.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"none\" stroke=\"{4}\" stroke-width=\"{5}\"/>\n",
                    stroke.Bounds.X,
                    stroke.Bounds.Y,
                    stroke.Bounds.Width,
                    stroke.Bounds.Height,
                    stroke.Colour.Hex,
                    stroke.Thickness);
                break;
            case GOverlayLineCommand line:
                output.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"{3}\" stroke=\"{4}\"/>\n",
                    line.X1,
                    line.Y1,
                    line.X2,
                    line.Y2,
                    line.Colour.Hex);
                break;
            case GOverlayTextCommand text:
                Text(output, text);
                break;
            case GOverlayArtworkCommand artwork:
                Artwork(output, artwork);
                break;
            case GOverlayMeterBarCommand meter:
                Meter(output, meter);
                break;
            case GOverlayWaterfallCommand waterfall:
                Waterfall(output, waterfall);
                break;
            case GOverlayProgressCommand progress:
                Progress(output, progress);
                break;
        }
    }

    private static void Text(StringBuilder output, GOverlayTextCommand text)
    {
        Rect(output, text.Bounds, text.Background);
        var anchor = text.Alignment switch
        {
            GOverlayTextAlignment.Centre => "middle",
            GOverlayTextAlignment.Right => "end",
            _ => "start"
        };
        var x = text.Alignment switch
        {
            GOverlayTextAlignment.Centre => text.Bounds.X + text.Bounds.Width / 2,
            GOverlayTextAlignment.Right => text.Bounds.Right,
            _ => text.Bounds.X
        };
        var y = text.Bounds.Y + Math.Min(
            text.Bounds.Height - 2,
            text.FontSize + 2);
        output.AppendFormat(
            CultureInfo.InvariantCulture,
            "<text x=\"{0}\" y=\"{1}\" fill=\"{2}\" font-size=\"{3}\" text-anchor=\"{4}\">{5}</text>\n",
            x,
            y,
            text.Colour.Hex,
            text.FontSize,
            anchor,
            SecurityElement.Escape(text.Text));
    }

    private static void Artwork(
        StringBuilder output,
        GOverlayArtworkCommand artwork)
    {
        Rect(output, artwork.Bounds, artwork.Background);
        if (artwork.HasArtwork)
        {
            var contentType = string.IsNullOrWhiteSpace(artwork.ContentType)
                ? "image/png"
                : artwork.ContentType;
            output.AppendFormat(
                CultureInfo.InvariantCulture,
                "<image x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" preserveAspectRatio=\"xMidYMid slice\" href=\"data:{4};base64,{5}\"/>\n",
                artwork.Bounds.X,
                artwork.Bounds.Y,
                artwork.Bounds.Width,
                artwork.Bounds.Height,
                SecurityElement.Escape(contentType),
                Convert.ToBase64String(artwork.ArtworkData));
            return;
        }

        output.AppendFormat(
            CultureInfo.InvariantCulture,
            "<line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"{3}\" stroke=\"{4}\" opacity=\".35\"/>\n",
            artwork.Bounds.X,
            artwork.Bounds.Y,
            artwork.Bounds.Right,
            artwork.Bounds.Bottom,
            artwork.Foreground.Hex);
        output.AppendFormat(
            CultureInfo.InvariantCulture,
            "<line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"{3}\" stroke=\"{4}\" opacity=\".35\"/>\n",
            artwork.Bounds.Right,
            artwork.Bounds.Y,
            artwork.Bounds.X,
            artwork.Bounds.Bottom,
            artwork.Foreground.Hex);
        output.AppendFormat(
            CultureInfo.InvariantCulture,
            "<text x=\"{0}\" y=\"{1}\" fill=\"{2}\" opacity=\".7\" font-size=\"13\" text-anchor=\"middle\">ALBUM ART</text>\n",
            artwork.Bounds.X + artwork.Bounds.Width / 2,
            artwork.Bounds.Y + artwork.Bounds.Height / 2 + 5,
            artwork.Foreground.Hex);
    }

    private static void Meter(
        StringBuilder output,
        GOverlayMeterBarCommand meter)
    {
        Rect(output, meter.Bounds, meter.Background);
        var height = Math.Max(1, (int)Math.Round(meter.Bounds.Height * meter.Value));
        Rect(
            output,
            new(
                meter.Bounds.X,
                meter.Bounds.Bottom - height,
                meter.Bounds.Width,
                height),
            meter.Foreground);
    }

    private static void Waterfall(
        StringBuilder output,
        GOverlayWaterfallCommand waterfall)
    {
        Rect(
            output,
            waterfall.Bounds,
            waterfall.ColourFor(0));
        if (!waterfall.HasColumn)
            return;

        var rowHeight =
            waterfall.Bounds.Height / GOverlayWaterfallGeometry.BandCount;
        var x = waterfall.Bounds.X
            + (waterfall.WritePosition
                * waterfall.ColumnWidth);
        for (var band = 0;
             band < GOverlayWaterfallGeometry.BandCount;
             band++)
        {
            var value = band < waterfall.Bands.Count
                ? waterfall.Bands[band]
                : 0;
            var visualRow =
                GOverlayWaterfallGeometry.BandCount - band - 1;
            Rect(
                output,
                new(
                    x,
                    waterfall.Bounds.Y + (visualRow * rowHeight),
                    waterfall.ColumnWidth,
                    rowHeight),
                waterfall.ColourFor(value));
        }

        Rect(
            output,
            waterfall.WriteCursorBounds,
            waterfall.WriteCursorColour);
    }

    private static void Progress(
        StringBuilder output,
        GOverlayProgressCommand progress)
    {
        Rect(output, progress.Bounds, progress.Background);
        var width = (int)Math.Round(progress.Bounds.Width * progress.Value);
        if (width > 0)
            Rect(
                output,
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

    private static void Rect(
        StringBuilder output,
        GOverlayRectangle bounds,
        GOverlayColour colour) =>
        output.AppendFormat(
            CultureInfo.InvariantCulture,
            "<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"{4}\"/>\n",
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            colour.Hex);
}
