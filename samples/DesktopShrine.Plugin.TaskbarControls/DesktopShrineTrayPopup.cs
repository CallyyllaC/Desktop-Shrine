using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopShrine.Plugin.TaskbarControls;

internal sealed class DesktopShrineTrayPopup : Form
{
    private const int LogicalWidth = 420;
    private readonly Image logo;
    private readonly List<ModernChoiceControl> choices = [];
    private readonly System.Windows.Forms.Timer dismissTimer;
    private int nextY = 96;

    public DesktopShrineTrayPopup(Image logo)
    {
        this.logo = logo;
        AutoScaleDimensions = new(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = DesktopShrineTheme.Surface;
        ClientSize = new(LogicalWidth, 574);
        Font = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "DesktopShrineTrayPopup";
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Desktop Shrine";
        TopMost = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);

        Controls.Add(new TrayHeaderControl(logo)
        {
            Location = new(16, 8),
            Size = new(LogicalWidth - 32, 88)
        });

        dismissTimer = new() { Interval = 80 };
        dismissTimer.Tick += (_, _) =>
        {
            dismissTimer.Stop();
            if (!ContainsFocus && !choices.Any(choice => choice.IsDropDownOpen))
                Hide();
        };
    }

    public event Action? Opening;

    protected override CreateParams CreateParams
    {
        get
        {
            const int classStyleDropShadow = 0x00020000;
            var parameters = base.CreateParams;
            parameters.ClassStyle |= classStyleDropShadow;
            return parameters;
        }
    }

    public void AddControl(Control control)
    {
        control.Font = Font;
        control.Location = new(16, nextY);
        control.Size = new(LogicalWidth - 32, control.Height);
        Controls.Add(control);
        nextY += control.Height;
        if (control is ModernChoiceControl choice)
        {
            choices.Add(choice);
            choice.FlyoutClosed += ScheduleDismissWhenInactive;
        }
        ResizeToContent();
    }

    public void AddCommand(TrayActionRow command)
    {
        command.Font = Font;
        command.Location = new(16, nextY);
        command.Size = new(LogicalWidth - 32, 54);
        Controls.Add(command);
        nextY += command.Height;
        ResizeToContent();
    }

    public void ToggleAt(Point anchor)
    {
        if (Visible)
        {
            Hide();
            return;
        }
        ShowAt(anchor);
    }

    public void ShowAt(Point anchor)
    {
        Opening?.Invoke();
        FitToWorkingArea(Screen.FromPoint(anchor).WorkingArea);
        Location = CalculateLocation(anchor);
        Show();
        // Per-monitor DPI scaling is finalised when the HWND is shown. Clamp
        // again using the scaled size so secondary displays remain in-bounds.
        Location = CalculateLocation(anchor);
        Activate();
        BringToFront();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(DesktopShrineTheme.Surface);

        var borderBounds = new RectangleF(.5f, .5f, ClientSize.Width - 1f, ClientSize.Height - 1f);
        using (var border = new Pen(DesktopShrineTheme.Border, 1f))
            ShrineDrawing.DrawRoundedRectangle(graphics, border, borderBounds, 14);

        using var accent = new LinearGradientBrush(
            new RectangleF(18, Height - 3, Width - 36, 2),
            DesktopShrineTheme.Cyan,
            DesktopShrineTheme.Magenta,
            LinearGradientMode.Horizontal);
        using var accentPen = new Pen(accent, 1.4f);
        graphics.DrawLine(accentPen, 18, Height - 2, Width - 18, Height - 2);
    }

    protected override void OnDeactivate(EventArgs args)
    {
        base.OnDeactivate(args);
        ScheduleDismissWhenInactive();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (args.KeyCode == Keys.Escape)
        {
            args.Handled = true;
            Hide();
            return;
        }
        base.OnKeyDown(args);
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        ApplyRoundedRegion();
        TryEnableModernWindowChrome();
    }

    protected override void OnSizeChanged(EventArgs args)
    {
        base.OnSizeChanged(args);
        if (IsHandleCreated)
            ApplyRoundedRegion();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dismissTimer.Dispose();
            logo.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ResizeToContent() => ClientSize = new(LogicalWidth, nextY + 14);

    private void FitToWorkingArea(Rectangle workArea)
    {
        var desiredHeight = nextY + 14;
        var availableHeight = Math.Max(240, workArea.Height - 16);
        AutoScroll = desiredHeight > availableHeight;
        AutoScrollMinSize = AutoScroll ? new(LogicalWidth - 20, desiredHeight) : Size.Empty;
        ClientSize = new(LogicalWidth, Math.Min(desiredHeight, availableHeight));
    }

    private Point CalculateLocation(Point anchor)
    {
        var workArea = Screen.FromPoint(anchor).WorkingArea;
        var x = Math.Clamp(
            anchor.X - Width + 20,
            workArea.Left + 8,
            workArea.Right - Width - 8);
        var y = anchor.Y - Height - 10;
        if (y < workArea.Top + 8)
            y = anchor.Y + 10;
        y = Math.Clamp(y, workArea.Top + 8, workArea.Bottom - Height - 8);
        return new(x, y);
    }

    private void ScheduleDismissWhenInactive()
    {
        if (ContainsFocus || choices.Any(choice => choice.IsDropDownOpen))
            return;
        dismissTimer.Stop();
        dismissTimer.Start();
    }

    private void ApplyRoundedRegion()
    {
        using var path = ShrineDrawing.RoundedRectangle(
            new RectangleF(0, 0, ClientSize.Width, ClientSize.Height),
            14);
        Region?.Dispose();
        Region = new Region(path);
    }

    private void TryEnableModernWindowChrome()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            const int darkModeAttribute = 20;
            const int cornerPreferenceAttribute = 33;
            var enabled = 1;
            var rounded = 2;
            _ = DwmSetWindowAttribute(Handle, darkModeAttribute, ref enabled, sizeof(int));
            _ = DwmSetWindowAttribute(Handle, cornerPreferenceAttribute, ref rounded, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize);
}

internal sealed class TrayHeaderControl : Control
{
    private readonly Image logo;

    public TrayHeaderControl(Image logo)
    {
        this.logo = logo;
        BackColor = DesktopShrineTheme.Surface;
        TabStop = false;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.Clear(DesktopShrineTheme.Surface);
        graphics.DrawImage(logo, new Rectangle(9, 8, 57, 57));

        using var titleFont = new Font(Font.FontFamily, 17f, FontStyle.Regular, GraphicsUnit.Point);
        using var statusFont = new Font(Font.FontFamily, 10.5f, FontStyle.Regular, GraphicsUnit.Point);
        TextRenderer.DrawText(
            graphics,
            "Desktop Shrine",
            titleFont,
            new Rectangle(82, 8, Width - 94, 34),
            DesktopShrineTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(
            graphics,
            DesktopShrineProductInfo.VersionDisplay,
            statusFont,
            new Rectangle(84, 43, Width - 96, 27),
            DesktopShrineTheme.Cyan,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        using var divider = new Pen(DesktopShrineTheme.Divider);
        graphics.DrawLine(divider, 9, Height - 1, Width - 9, Height - 1);
    }
}

internal sealed class TrayActionRow : Control
{
    private readonly ShrineGlyph glyph;
    private bool pointerOver;
    private bool pressed;

    public TrayActionRow(string text, ShrineGlyph glyph)
    {
        Text = text;
        this.glyph = glyph;
        AccessibleName = text;
        AccessibleRole = AccessibleRole.PushButton;
        BackColor = DesktopShrineTheme.Surface;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
    }

    internal bool IsPointerHighlightVisible => pointerOver;

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(DesktopShrineTheme.Surface);
        if (pointerOver || pressed)
        {
            using var hover = new SolidBrush(pressed
                ? DesktopShrineTheme.Pressed
                : DesktopShrineTheme.Hover);
            ShrineDrawing.FillRoundedRectangle(
                graphics,
                hover,
                new RectangleF(3, 3, Width - 6, Height - 6),
                8);
        }
        ShrineDrawing.DrawGlyph(
            graphics,
            glyph,
            new RectangleF(11, 15, 25, 25),
            DesktopShrineTheme.Magenta,
            1.9f);
        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            new Rectangle(52, 0, Width - 66, Height),
            DesktopShrineTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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
                7);
        }
    }

    protected override void OnMouseEnter(EventArgs args)
    {
        base.OnMouseEnter(args);
        pointerOver = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        pointerOver = false;
        pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left)
            return;
        Focus();
        pressed = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.Button != MouseButtons.Left)
            return;
        var invoke = pressed && ClientRectangle.Contains(args.Location);
        pressed = false;
        Capture = false;
        Invalidate();
        if (invoke)
            OnClick(EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (args.KeyCode is Keys.Enter or Keys.Space)
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
            OnClick(EventArgs.Empty);
            return;
        }
        base.OnKeyDown(args);
    }

    protected override void OnGotFocus(EventArgs args)
    {
        base.OnGotFocus(args);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs args)
    {
        base.OnLostFocus(args);
        pressed = false;
        Invalidate();
    }
}
