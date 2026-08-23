using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DesktopShrine.Plugin.TaskbarControls;

internal sealed class ModernSliderControl : Control
{
    internal const int SliderMaximum = 1000;

    private readonly TaskbarSliderDefinition definition;
    private float currentValue;
    private bool dragging;
    private bool pointerOverTrack;

    public ModernSliderControl(TaskbarSliderDefinition definition, float value)
    {
        this.definition = definition;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
        BackColor = DesktopShrineTheme.Surface;
        ForeColor = DesktopShrineTheme.Text;
        AccessibleName = definition.DisplayName;
        AccessibleRole = AccessibleRole.Slider;
        Margin = Padding.Empty;
        Size = new(388, 112);
        TabStop = true;
        SetValue(value);
    }

    public event Action<float>? ValueChanged;
    public float Value => currentValue;

    public void SetValue(float value)
    {
        currentValue = definition.Quantise(value);
        AccessibleDescription = definition.FormatValue(currentValue);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(DesktopShrineTheme.Surface);

        ShrineDrawing.DrawGlyph(
            graphics,
            definition.Id.Contains("gamma", StringComparison.OrdinalIgnoreCase)
                ? ShrineGlyph.Gamma
                : ShrineGlyph.Brightness,
            new RectangleF(10, 17, 27, 27),
            DesktopShrineTheme.Cyan,
            1.7f);

        TextRenderer.DrawText(
            graphics,
            definition.DisplayName,
            Font,
            new Rectangle(52, 15, Width - 142, 31),
            DesktopShrineTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(
            graphics,
            definition.FormatValue(currentValue),
            Font,
            new Rectangle(Width - 82, 15, 66, 31),
            DesktopShrineTheme.Cyan,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix);

        var track = TrackBounds;
        using (var trackBrush = new SolidBrush(DesktopShrineTheme.Track))
            ShrineDrawing.FillRoundedRectangle(graphics, trackBrush, track, track.Height / 2f);

        using (var tickPen = new Pen(Color.FromArgb(56, 122, 144, 169), 1f))
        {
            for (var index = 1; index < 10; index++)
            {
                var x = track.Left + track.Width * index / 10f;
                graphics.DrawLine(tickPen, x, track.Top - 2, x, track.Bottom + 2);
            }
        }

        var position = definition.PositionFromValue(currentValue, SliderMaximum);
        var ratio = position / (float)SliderMaximum;
        var fillWidth = Math.Max(track.Height, track.Width * ratio);
        using (var gradient = new LinearGradientBrush(
                   track,
                   DesktopShrineTheme.Cyan,
                   DesktopShrineTheme.Magenta,
                   LinearGradientMode.Horizontal))
        {
            gradient.InterpolationColors = new ColorBlend
            {
                Colors =
                [
                    DesktopShrineTheme.Cyan,
                    DesktopShrineTheme.Blue,
                    DesktopShrineTheme.Magenta
                ],
                Positions = [0f, .52f, 1f]
            };
            graphics.SetClip(new RectangleF(track.Left, track.Top, fillWidth, track.Height));
            ShrineDrawing.FillRoundedRectangle(graphics, gradient, track, track.Height / 2f);
            graphics.ResetClip();
        }

        var knobX = track.Left + track.Width * ratio;
        var knob = new RectangleF(knobX - 10, track.Top - 7, 20, 20);
        if (pointerOverTrack || dragging || (Focused && ShowFocusCues))
        {
            using var glow = new SolidBrush(Color.FromArgb(45, DesktopShrineTheme.Cyan));
            graphics.FillEllipse(glow, knob.X - 5, knob.Y - 5, knob.Width + 10, knob.Height + 10);
        }
        using (var shadow = new SolidBrush(Color.FromArgb(100, Color.Black)))
            graphics.FillEllipse(shadow, knob.X + 1, knob.Y + 2, knob.Width, knob.Height);
        using (var knobBrush = new SolidBrush(Color.FromArgb(235, 240, 246)))
            graphics.FillEllipse(knobBrush, knob);
        using (var outline = new Pen(DesktopShrineTheme.Cyan, 1.6f))
            graphics.DrawEllipse(outline, knob);

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Color.FromArgb(170, DesktopShrineTheme.Cyan))
            {
                DashStyle = DashStyle.Dot
            };
            ShrineDrawing.DrawRoundedRectangle(
                graphics,
                focus,
                new RectangleF(4, 4, Width - 9, Height - 9),
                8);
        }

        using var divider = new Pen(DesktopShrineTheme.Divider);
        graphics.DrawLine(divider, 10, Height - 1, Width - 10, Height - 1);
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        var nowOver = TrackHitBounds.Contains(args.Location);
        if (nowOver != pointerOverTrack)
        {
            pointerOverTrack = nowOver;
            Invalidate();
        }
        Cursor = nowOver || dragging ? Cursors.Hand : Cursors.Default;
        if (dragging)
            SetFromMouse(args.X);
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        pointerOverTrack = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left || !TrackHitBounds.Contains(args.Location))
            return;
        Focus();
        dragging = true;
        Capture = true;
        SetFromMouse(args.X);
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.Button != MouseButtons.Left)
            return;
        dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        var handled = true;
        var value = args.KeyCode switch
        {
            Keys.Left or Keys.Down => currentValue - definition.Step,
            Keys.Right or Keys.Up => currentValue + definition.Step,
            Keys.PageDown => currentValue - 10f * definition.Step,
            Keys.PageUp => currentValue + 10f * definition.Step,
            Keys.Home => definition.Minimum,
            Keys.End => definition.Maximum,
            _ => currentValue
        };
        if (args.KeyCode is not (Keys.Left or Keys.Down or Keys.Right
                or Keys.Up or Keys.PageDown or Keys.PageUp
                or Keys.Home or Keys.End))
            handled = false;

        if (!handled)
        {
            base.OnKeyDown(args);
            return;
        }
        args.Handled = true;
        args.SuppressKeyPress = true;
        SetAndNotify(value);
    }

    protected override void OnMouseWheel(MouseEventArgs args)
    {
        if (args is HandledMouseEventArgs handled)
            handled.Handled = true;
        var direction = Math.Sign(args.Delta);
        if (direction != 0)
            SetAndNotify(currentValue + direction * definition.Step);
        base.OnMouseWheel(args);
    }

    protected override void OnGotFocus(EventArgs args)
    {
        base.OnGotFocus(args);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs args)
    {
        base.OnLostFocus(args);
        Invalidate();
    }

    private RectangleF TrackBounds => new(14, 75, Math.Max(1, Width - 28), 6);
    private Rectangle TrackHitBounds => Rectangle.Round(new RectangleF(
        TrackBounds.Left - 2,
        TrackBounds.Top - 14,
        TrackBounds.Width + 4,
        34));

    private void SetFromMouse(int x)
    {
        var track = TrackBounds;
        var ratio = Math.Clamp((x - track.Left) / track.Width, 0f, 1f);
        SetAndNotify(definition.ValueFromPosition(
            (int)Math.Round(ratio * SliderMaximum),
            SliderMaximum));
    }

    private void SetAndNotify(float value)
    {
        var previous = currentValue;
        SetValue(value);
        if (Math.Abs(previous - currentValue) >= 0.0001f)
            ValueChanged?.Invoke(currentValue);
    }
}
