using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DesktopShrine.Plugin.TaskbarControls;

internal sealed class ModernChoiceControl : Control
{
    private readonly TaskbarChoiceDefinition definition;
    private IReadOnlyList<TaskbarChoice> available = [];
    private string currentValue;
    private ToolStripDropDown? flyout;
    private bool pointerOverRow;
    private bool pressed;

    public ModernChoiceControl(TaskbarChoiceDefinition definition, string value)
    {
        this.definition = definition;
        currentValue = value;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
        AccessibleName = definition.DisplayName;
        AccessibleRole = AccessibleRole.ComboBox;
        BackColor = DesktopShrineTheme.Surface;
        ForeColor = DesktopShrineTheme.Text;
        Margin = Padding.Empty;
        Size = new(388, 132);
        TabStop = true;
        RefreshChoices();
    }

    public event Action<string>? ValueChanged;
    public event Action? FlyoutClosed;
    public string Value => currentValue;

    public void SetValue(string value)
    {
        currentValue = string.IsNullOrWhiteSpace(value) ? "default" : value;
        AccessibleDescription = CurrentDisplayName;
        Invalidate();
    }

    public void RefreshChoices()
    {
        try
        {
            available = definition.GetChoices();
        }
        catch
        {
            available = [new("default", "Audio devices unavailable")];
        }
        if (!available.Any(choice => string.Equals(
                choice.Value,
                currentValue,
                StringComparison.OrdinalIgnoreCase)))
        {
            available = available
                .Append(new TaskbarChoice(currentValue, "Current device (unavailable)"))
                .ToArray();
        }
        AccessibleDescription = CurrentDisplayName;
        Invalidate();
    }

    internal void OpenDropDownForTesting() => OpenFlyout();
    internal bool IsDropDownOpen => flyout?.Visible == true;
    internal ToolStripDropDown? FlyoutForTesting => flyout;
    internal bool IsPointerHighlightVisible => pointerOverRow;

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(DesktopShrineTheme.Surface);

        ShrineDrawing.DrawGlyph(
            graphics,
            ShrineGlyph.Audio,
            new RectangleF(10, 17, 28, 28),
            DesktopShrineTheme.Cyan,
            1.7f);
        TextRenderer.DrawText(
            graphics,
            definition.DisplayName,
            Font,
            new Rectangle(52, 15, Width - 68, 31),
            DesktopShrineTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        var row = SelectionBounds;
        using (var background = new SolidBrush(pressed
                   ? DesktopShrineTheme.Pressed
                   : pointerOverRow
                       ? DesktopShrineTheme.Hover
                       : DesktopShrineTheme.Background))
            ShrineDrawing.FillRoundedRectangle(graphics, background, row, 9);
        using (var border = new Pen(pointerOverRow
                   ? Color.FromArgb(105, DesktopShrineTheme.Cyan)
                   : DesktopShrineTheme.Border,
                   pointerOverRow ? 1.4f : 1f))
            ShrineDrawing.DrawRoundedRectangle(graphics, border, row, 9);

        TextRenderer.DrawText(
            graphics,
            CurrentDisplayName,
            Font,
            Rectangle.Round(new RectangleF(row.Left + 16, row.Top, row.Width - 52, row.Height)),
            DesktopShrineTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        ShrineDrawing.DrawGlyph(
            graphics,
            ShrineGlyph.Chevron,
            new RectangleF(row.Right - 31, row.Top + 17, 17, 22),
            DesktopShrineTheme.MutedText,
            2f);

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Color.FromArgb(180, DesktopShrineTheme.Cyan))
            {
                DashStyle = DashStyle.Dot
            };
            ShrineDrawing.DrawRoundedRectangle(
                graphics,
                focus,
                new RectangleF(row.X + 3, row.Y + 3, row.Width - 6, row.Height - 6),
                7);
        }

        using var divider = new Pen(DesktopShrineTheme.Divider);
        graphics.DrawLine(divider, 10, Height - 1, Width - 10, Height - 1);
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        var over = SelectionBounds.Contains(args.Location);
        if (over != pointerOverRow)
        {
            pointerOverRow = over;
            Invalidate();
        }
        Cursor = over ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        pointerOverRow = false;
        pressed = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left || !SelectionBounds.Contains(args.Location))
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
        var open = pressed && SelectionBounds.Contains(args.Location);
        pressed = false;
        Capture = false;
        Invalidate();
        if (open)
            OpenFlyout();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (args.KeyCode is Keys.Enter or Keys.Space or Keys.Down)
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
            OpenFlyout();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            flyout?.Dispose();
            flyout = null;
        }
        base.Dispose(disposing);
    }

    private RectangleF SelectionBounds => new(52, 52, Math.Max(1, Width - 66), 56);

    private string CurrentDisplayName => available.FirstOrDefault(choice =>
            string.Equals(choice.Value, currentValue, StringComparison.OrdinalIgnoreCase))
        ?.DisplayName ?? "Select an audio device";

    private void OpenFlyout()
    {
        if (flyout?.Visible == true)
        {
            flyout.Close();
            return;
        }

        RefreshChoices();
        flyout?.Dispose();
        var panelWidth = Math.Max((int)Math.Round(SelectionBounds.Width), 300);
        var visibleRows = Math.Clamp(available.Count, 1, 8);
        var panel = new Panel
        {
            AutoScroll = available.Count > visibleRows,
            BackColor = DesktopShrineTheme.Background,
            Font = Font,
            Margin = Padding.Empty,
            Size = new(panelWidth, visibleRows * 44)
        };
        for (var index = 0; index < available.Count; index++)
        {
            var choice = available[index];
            var row = new AudioDeviceOptionRow(
                choice,
                string.Equals(choice.Value, currentValue, StringComparison.OrdinalIgnoreCase))
            {
                Location = new(0, index * 44),
                Size = new(panelWidth, 44)
            };
            row.ChoiceInvoked += selected =>
            {
                flyout?.Close();
                if (string.Equals(
                        selected.Value,
                        currentValue,
                        StringComparison.OrdinalIgnoreCase))
                    return;
                currentValue = selected.Value;
                AccessibleDescription = selected.DisplayName;
                ValueChanged?.Invoke(selected.Value);
                Invalidate();
            };
            panel.Controls.Add(row);
        }

        var host = new ToolStripControlHost(panel)
        {
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Size = panel.Size
        };
        flyout = new ToolStripDropDown
        {
            AutoClose = true,
            AutoSize = false,
            BackColor = DesktopShrineTheme.Background,
            DropShadowEnabled = true,
            Margin = Padding.Empty,
            Padding = new(1),
            Renderer = new DesktopShrineMenuRenderer(),
            Size = new(panel.Width + 2, panel.Height + 2)
        };
        flyout.Items.Add(host);
        flyout.Opened += (_, _) =>
        {
            using var path = ShrineDrawing.RoundedRectangle(
                new RectangleF(0, 0, flyout.Width, flyout.Height),
                9);
            flyout.Region?.Dispose();
            flyout.Region = new Region(path);
        };
        flyout.Closed += (_, _) =>
        {
            pressed = false;
            Invalidate();
            FlyoutClosed?.Invoke();
        };
        var rowBounds = Rectangle.Round(SelectionBounds);
        flyout.Show(this, new Point(rowBounds.Left, rowBounds.Bottom + 4));
        panel.Controls.OfType<AudioDeviceOptionRow>().FirstOrDefault()?.Focus();
    }
}

internal sealed class AudioDeviceOptionRow : Control
{
    private readonly TaskbarChoice choice;
    private readonly bool selected;
    private bool pointerOver;
    private bool pressed;

    public AudioDeviceOptionRow(TaskbarChoice choice, bool selected)
    {
        this.choice = choice;
        this.selected = selected;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
        AccessibleName = choice.DisplayName;
        AccessibleRole = AccessibleRole.ListItem;
        BackColor = DesktopShrineTheme.Background;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public event Action<TaskbarChoice>? ChoiceInvoked;

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(pressed
            ? DesktopShrineTheme.Pressed
            : pointerOver
                ? DesktopShrineTheme.Hover
                : DesktopShrineTheme.Background);
        if (selected)
        {
            ShrineDrawing.DrawGlyph(
                graphics,
                ShrineGlyph.Check,
                new RectangleF(12, 14, 16, 16),
                DesktopShrineTheme.Cyan,
                1.8f);
        }
        TextRenderer.DrawText(
            graphics,
            choice.DisplayName,
            Font,
            new Rectangle(38, 0, Width - 50, Height),
            selected ? DesktopShrineTheme.Text : Color.FromArgb(220, DesktopShrineTheme.Text),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Color.FromArgb(175, DesktopShrineTheme.Cyan))
            {
                DashStyle = DashStyle.Dot
            };
            graphics.DrawRectangle(focus, 4, 4, Width - 9, Height - 9);
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
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.Button != MouseButtons.Left)
            return;
        var invoke = pressed && ClientRectangle.Contains(args.Location);
        pressed = false;
        Invalidate();
        if (invoke)
            ChoiceInvoked?.Invoke(choice);
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (args.KeyCode is Keys.Enter or Keys.Space)
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
            ChoiceInvoked?.Invoke(choice);
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
