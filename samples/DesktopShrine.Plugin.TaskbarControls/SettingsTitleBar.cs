using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopShrine.Plugin.TaskbarControls;

internal sealed class SettingsTitleBar : Control
{
    public const int LogicalHeight = 36;
    private readonly Form owner;

    public SettingsTitleBar(Form owner)
    {
        this.owner = owner;
        BackColor = DesktopShrineTheme.SurfaceRaised;
        Dock = DockStyle.Fill;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);

        Controls.Add(CreateCaptionButton("—", "Minimise", () => owner.WindowState = FormWindowState.Minimized));
        Controls.Add(CreateCaptionButton("□", "Maximise or restore", ToggleMaximise));
        Controls.Add(CreateCaptionButton("×", "Close", () => owner.Close(), true));
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        args.Graphics.Clear(DesktopShrineTheme.SurfaceRaised);
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var accent = new LinearGradientBrush(
            new RectangleF(0, 0, Width, 2),
            DesktopShrineTheme.Cyan,
            DesktopShrineTheme.Magenta,
            LinearGradientMode.Horizontal);
        args.Graphics.FillRectangle(accent, 0, 0, Width, 2);
        TextRenderer.DrawText(args.Graphics,
            $"Desktop Shrine Settings — {DesktopShrineProductInfo.VersionDisplay}",
            Font,
            new Rectangle(14, 2, Math.Max(1, Width - 160), Height - 2),
            DesktopShrineTheme.MutedText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left)
            return;

        _ = ReleaseCapture();
        _ = SendMessage(
            owner.Handle,
            WindowMessages.NonClientLeftButtonDown,
            HitTest.Caption,
            0);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs args)
    {
        base.OnMouseDoubleClick(args);
        if (args.Button == MouseButtons.Left)
            ToggleMaximise();
    }

    private Button CreateCaptionButton(
        string text,
        string accessibleName,
        Action action,
        bool close = false)
    {
        var button = new Button
        {
            AccessibleName = accessibleName,
            BackColor = DesktopShrineTheme.SurfaceRaised,
            Cursor = Cursors.Hand,
            Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Symbol", 10f),
            ForeColor = DesktopShrineTheme.Text,
            Size = new(46, LogicalHeight),
            TabStop = true,
            Text = text,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = close
            ? Color.FromArgb(175, 36, 62)
            : DesktopShrineTheme.Hover;
        button.FlatAppearance.MouseDownBackColor = close
            ? Color.FromArgb(205, 31, 61)
            : DesktopShrineTheme.Pressed;
        button.Click += (_, _) => action();
        return button;
    }

    private void ToggleMaximise() => owner.WindowState =
        owner.WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(
        nint window,
        int message,
        int wParam,
        int lParam);

    private static class WindowMessages
    {
        public const int NonClientLeftButtonDown = 0x00A1;
    }

    private static class HitTest
    {
        public const int Caption = 2;
    }
}
