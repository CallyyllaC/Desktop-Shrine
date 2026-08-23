using System.Drawing;
using System.Drawing.Drawing2D;

namespace DesktopShrine.Plugin.TaskbarControls;

internal enum ShrineGlyph
{
    Brightness,
    Gamma,
    Audio,
    Restart,
    Power,
    Chevron,
    Check
}

internal static class ShrineDrawing
{
    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRoundedRectangle(
        Graphics graphics,
        Brush brush,
        RectangleF bounds,
        float radius)
    {
        using var path = RoundedRectangle(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(
        Graphics graphics,
        Pen pen,
        RectangleF bounds,
        float radius)
    {
        using var path = RoundedRectangle(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    public static void DrawGlyph(
        Graphics graphics,
        ShrineGlyph glyph,
        RectangleF bounds,
        Color colour,
        float width = 1.8f)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(colour, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        switch (glyph)
        {
            case ShrineGlyph.Brightness:
                DrawBrightness(graphics, pen, bounds);
                break;
            case ShrineGlyph.Gamma:
                DrawGamma(graphics, pen, bounds);
                break;
            case ShrineGlyph.Audio:
                DrawAudio(graphics, pen, bounds);
                break;
            case ShrineGlyph.Restart:
                DrawRestart(graphics, pen, bounds);
                break;
            case ShrineGlyph.Power:
                DrawPower(graphics, pen, bounds);
                break;
            case ShrineGlyph.Chevron:
                graphics.DrawLines(pen,
                new PointF[]
                {
                    new(bounds.Left + bounds.Width * .34f, bounds.Top + bounds.Height * .22f),
                    new(bounds.Left + bounds.Width * .66f, bounds.Top + bounds.Height * .5f),
                    new(bounds.Left + bounds.Width * .34f, bounds.Top + bounds.Height * .78f)
                });
                break;
            case ShrineGlyph.Check:
                graphics.DrawLines(pen,
                new PointF[]
                {
                    new(bounds.Left + bounds.Width * .18f, bounds.Top + bounds.Height * .52f),
                    new(bounds.Left + bounds.Width * .42f, bounds.Top + bounds.Height * .74f),
                    new(bounds.Left + bounds.Width * .82f, bounds.Top + bounds.Height * .25f)
                });
                break;
        }
    }

    private static void DrawBrightness(Graphics graphics, Pen pen, RectangleF bounds)
    {
        var centre = new PointF(bounds.Left + bounds.Width / 2f, bounds.Top + bounds.Height / 2f);
        var radius = Math.Min(bounds.Width, bounds.Height) * .2f;
        graphics.DrawEllipse(pen, centre.X - radius, centre.Y - radius, radius * 2f, radius * 2f);
        for (var index = 0; index < 8; index++)
        {
            var angle = index * Math.PI / 4d;
            var inner = radius * 1.65f;
            var outer = radius * 2.3f;
            graphics.DrawLine(
                pen,
                centre.X + (float)Math.Cos(angle) * inner,
                centre.Y + (float)Math.Sin(angle) * inner,
                centre.X + (float)Math.Cos(angle) * outer,
                centre.Y + (float)Math.Sin(angle) * outer);
        }
    }

    private static void DrawGamma(Graphics graphics, Pen pen, RectangleF bounds)
    {
        var left = bounds.Left + bounds.Width * .13f;
        var bottom = bounds.Bottom - bounds.Height * .14f;
        graphics.DrawLine(pen, left, bounds.Top + bounds.Height * .1f, left, bottom);
        graphics.DrawLine(pen, left, bottom, bounds.Right - bounds.Width * .08f, bottom);
        using var path = new GraphicsPath();
        path.AddBezier(
            left + bounds.Width * .08f,
            bottom - bounds.Height * .05f,
            left + bounds.Width * .2f,
            bottom - bounds.Height * .48f,
            left + bounds.Width * .42f,
            bounds.Top + bounds.Height * .2f,
            bounds.Right - bounds.Width * .08f,
            bounds.Top + bounds.Height * .18f);
        graphics.DrawPath(pen, path);
    }

    private static void DrawAudio(Graphics graphics, Pen pen, RectangleF bounds)
    {
        var speaker = new[]
        {
            new PointF(bounds.Left + bounds.Width * .08f, bounds.Top + bounds.Height * .4f),
            new PointF(bounds.Left + bounds.Width * .28f, bounds.Top + bounds.Height * .4f),
            new PointF(bounds.Left + bounds.Width * .52f, bounds.Top + bounds.Height * .18f),
            new PointF(bounds.Left + bounds.Width * .52f, bounds.Top + bounds.Height * .82f),
            new PointF(bounds.Left + bounds.Width * .28f, bounds.Top + bounds.Height * .6f),
            new PointF(bounds.Left + bounds.Width * .08f, bounds.Top + bounds.Height * .6f)
        };
        graphics.DrawPolygon(pen, speaker);
        graphics.DrawArc(pen, bounds.Left + bounds.Width * .45f, bounds.Top + bounds.Height * .29f,
            bounds.Width * .3f, bounds.Height * .42f, -55, 110);
        graphics.DrawArc(pen, bounds.Left + bounds.Width * .43f, bounds.Top + bounds.Height * .15f,
            bounds.Width * .48f, bounds.Height * .7f, -52, 104);
    }

    private static void DrawRestart(Graphics graphics, Pen pen, RectangleF bounds)
    {
        graphics.DrawArc(pen, bounds.Left + bounds.Width * .12f, bounds.Top + bounds.Height * .12f,
            bounds.Width * .7f, bounds.Height * .7f, 35, 290);
        graphics.DrawLines(pen,
        new PointF[]
        {
            new(bounds.Right - bounds.Width * .06f, bounds.Top + bounds.Height * .08f),
            new(bounds.Right - bounds.Width * .08f, bounds.Top + bounds.Height * .36f),
            new(bounds.Right - bounds.Width * .34f, bounds.Top + bounds.Height * .24f)
        });
    }

    private static void DrawPower(Graphics graphics, Pen pen, RectangleF bounds)
    {
        graphics.DrawArc(pen, bounds.Left + bounds.Width * .12f, bounds.Top + bounds.Height * .12f,
            bounds.Width * .76f, bounds.Height * .76f, -52, 284);
        graphics.DrawLine(pen,
            bounds.Left + bounds.Width * .5f,
            bounds.Top + bounds.Height * .04f,
            bounds.Left + bounds.Width * .5f,
            bounds.Top + bounds.Height * .48f);
    }
}
