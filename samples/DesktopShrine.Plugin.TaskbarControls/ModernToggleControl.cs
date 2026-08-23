using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DesktopShrine.Plugin.TaskbarControls;

internal sealed class ModernToggleControl : Control
{
    private readonly TaskbarToggleDefinition definition;
    private bool value;
    private bool pointerOver;

    public ModernToggleControl(TaskbarToggleDefinition definition, bool value)
    {
        this.definition = definition;
        this.value = value;
        AccessibleName = definition.DisplayName;
        AccessibleRole = AccessibleRole.CheckButton;
        AccessibleDescription = value ? "On" : "Off";
        BackColor = DesktopShrineTheme.Surface;
        Cursor = Cursors.Hand;
        Size = new(388, 70);
        TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable | ControlStyles.UserPaint, true);
    }

    public event Action<bool>? ValueChanged;
    public bool Value => value;

    public void SetValue(bool next)
    {
        value = next;
        AccessibleDescription = value ? "On" : "Off";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        args.Graphics.Clear(DesktopShrineTheme.Surface);
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        ShrineDrawing.DrawGlyph(args.Graphics, ShrineGlyph.Check,
            new RectangleF(11, 22, 25, 25), DesktopShrineTheme.Cyan, 1.8f);
        TextRenderer.DrawText(args.Graphics, definition.DisplayName, Font,
            new Rectangle(52, 0, Width - 130, Height), DesktopShrineTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var track = new RectangleF(Width - 72, 21, 52, 28);
        using var fill = new SolidBrush(value
            ? DesktopShrineTheme.Cyan
            : pointerOver ? DesktopShrineTheme.Hover : DesktopShrineTheme.Track);
        ShrineDrawing.FillRoundedRectangle(args.Graphics, fill, track, 14);
        var knob = new RectangleF(value ? track.Right - 24 : track.Left + 4,
            track.Top + 4, 20, 20);
        using var knobBrush = new SolidBrush(DesktopShrineTheme.Text);
        args.Graphics.FillEllipse(knobBrush, knob);
        using var divider = new Pen(DesktopShrineTheme.Divider);
        args.Graphics.DrawLine(divider, 10, Height - 1, Width - 10, Height - 1);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e); pointerOver = true; Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e); pointerOver = false; Invalidate();
    }
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e); Toggle();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true; e.SuppressKeyPress = true; Toggle(); return;
        }
        base.OnKeyDown(e);
    }
    private void Toggle()
    {
        SetValue(!value);
        ValueChanged?.Invoke(value);
    }
}
