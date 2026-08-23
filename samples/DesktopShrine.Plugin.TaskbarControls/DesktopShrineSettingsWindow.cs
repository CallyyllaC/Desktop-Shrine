using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace DesktopShrine.Plugin.TaskbarControls;

internal sealed class DesktopShrineSettingsWindow : Form
{
    private readonly DesktopShrineSettingsDefinition definition;
    private readonly Image logo;
    private readonly FlowLayoutPanel navigation;
    private readonly BrandedFlowLayoutPanel content;
    private readonly Label status;
    private readonly Dictionary<string, Button> quickButtons = [];
    private HashSet<string> quickAccess;
    private Label? quickCountLabel;
    private Action? currentPage;
    private bool allowClose;

    public DesktopShrineSettingsWindow(Image logo, DesktopShrineSettingsDefinition definition)
    {
        this.logo = logo;
        this.definition = definition;
        quickAccess = new(definition.ReadQuickAccess(), StringComparer.OrdinalIgnoreCase);
        currentPage?.Invoke();
        AutoScaleDimensions = new(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = DesktopShrineTheme.Background;
        ClientSize = new(980, 720);
        Font = new Font("Segoe UI", 10f);
        ForeColor = DesktopShrineTheme.Text;
        FormBorderStyle = FormBorderStyle.None;
        Icon = WindowsTaskbarIcon.LoadProductIcon();
        MinimumSize = new(820, 600);
        Padding = new(1);
        StartPosition = FormStartPosition.CenterScreen;
        Text = $"Desktop Shrine Settings — {DesktopShrineProductInfo.VersionDisplay}";

        var header = CreateHeader();

        navigation = new FlowLayoutPanel
        {
            BackColor = DesktopShrineTheme.Surface,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new(14, 18, 14, 14),
            WrapContents = false
        };

        content = new BrandedFlowLayoutPanel
        {
            AutoScroll = true,
            BackColor = DesktopShrineTheme.Background,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new(28, 22, 22, 24),
            WrapContents = false
        };
        var body = new TableLayoutPanel
        {
            BackColor = DesktopShrineTheme.Background,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            RowCount = 1
        };
        body.ColumnStyles.Add(new(SizeType.Absolute, 230));
        body.ColumnStyles.Add(new(SizeType.Percent, 100));
        body.RowStyles.Add(new(SizeType.Percent, 100));
        body.Controls.Add(navigation, 0, 0);
        var contentHost = new Panel
        {
            BackColor = DesktopShrineTheme.Background,
            Dock = DockStyle.Fill
        };
        contentHost.Controls.Add(content);
        contentHost.Controls.Add(new ModernVerticalScrollBar(content)
        {
            Dock = DockStyle.Right
        });
        body.Controls.Add(contentHost, 1, 0);
        var shell = new TableLayoutPanel
        {
            BackColor = DesktopShrineTheme.Background,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 3
        };
        shell.ColumnStyles.Add(new(SizeType.Percent, 100));
        shell.RowStyles.Add(new(SizeType.Absolute, SettingsTitleBar.LogicalHeight));
        shell.RowStyles.Add(new(SizeType.Absolute, 82));
        shell.RowStyles.Add(new(SizeType.Percent, 100));
        shell.Controls.Add(new SettingsTitleBar(this), 0, 0);
        shell.Controls.Add(header, 0, 1);
        shell.Controls.Add(body, 0, 2);
        Controls.Add(shell);

        status = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = DesktopShrineTheme.MutedText,
            Location = new(450, 51),
            Size = new(500, 22),
            TextAlign = ContentAlignment.MiddleRight
        };
        header.Controls.Add(status);

        foreach (var group in definition.Groups)
        {
            var captured = group;
            navigation.Controls.Add(CreateNavigationButton(group.DisplayName,
                () => ShowPlugin(captured)));
        }
        navigation.Controls.Add(CreateNavigationButton("Application", ShowApplication));
        if (definition.Groups.Count > 0) ShowPlugin(definition.Groups[0]);
        else ShowApplication();

        FormClosing += (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            Hide();
        };
    }

    public void ShowAndActivate()
    {
        quickAccess = new(definition.ReadQuickAccess(), StringComparer.OrdinalIgnoreCase);
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    public void CloseForApplication()
    {
        allowClose = true;
        Close();
    }

    private Panel CreateHeader()
    {
        var panel = new Panel { BackColor = DesktopShrineTheme.Surface, Dock = DockStyle.Fill, Height = 82 };
        panel.Controls.Add(new PictureBox
        {
            Image = logo, Location = new(22, 13), Size = new(54, 54), SizeMode = PictureBoxSizeMode.Zoom
        });
        panel.Controls.Add(new Label
        {
            AutoSize = true, Font = new Font(Font.FontFamily, 17f), ForeColor = DesktopShrineTheme.Text,
            Location = new(92, 14), Text = "Desktop Shrine"
        });
        panel.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = DesktopShrineTheme.Cyan, Location = new(94, 49),
            Text = DesktopShrineProductInfo.VersionDisplay
        });
        return panel;
    }

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

    protected override void WndProc(ref Message message)
    {
        const int hitTestMessage = 0x0084;
        if (message.Msg == hitTestMessage && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref message);
            if ((int)message.Result != 1)
                return;
            var packed = message.LParam.ToInt64();
            var screenPoint = new Point(
                unchecked((short)(packed & 0xffff)),
                unchecked((short)((packed >> 16) & 0xffff)));
            var point = PointToClient(screenPoint);
            const int edge = 7;
            var left = point.X < edge;
            var right = point.X >= ClientSize.Width - edge;
            var top = point.Y < edge;
            var bottom = point.Y >= ClientSize.Height - edge;
            message.Result = (nint)(top && left ? 13
                : top && right ? 14
                : bottom && left ? 16
                : bottom && right ? 17
                : left ? 10
                : right ? 11
                : top ? 12
                : bottom ? 15
                : point.Y < SettingsTitleBar.LogicalHeight
                    && point.X < ClientSize.Width - 138 ? 2
                : 1);
            return;
        }
        base.WndProc(ref message);
    }

    private Button CreateNavigationButton(string text, Action action)
    {
        var button = StyledButton(text);
        button.Width = 198;
        button.Height = 46;
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Click += (_, _) => action();
        return button;
    }

    private void ShowPlugin(PluginSettingsGroup group)
    {
        currentPage = () => ShowPlugin(group);
        BeginPage(group.DisplayName, group.Description);
        foreach (var category in group.Settings.Select(setting => setting.Category).Distinct())
        {
            AddSectionHeading(category);
            foreach (var setting in group.Settings.Where(setting => setting.Category == category))
                AddSetting(setting);
        }
        if (group.Settings.Any(setting => setting.RequiresRestart))
            AddRestartNote();
    }

    private void BeginPage(string title, string? description)
    {
        content.SuspendLayout();
        content.SetBrandedScrollPosition(0);
        while (content.Controls.Count > 0)
            content.Controls[0].Dispose();
        quickButtons.Clear();
        content.Controls.Add(new Label
        {
            AutoSize = false, Font = new Font(Font.FontFamily, 22f), ForeColor = DesktopShrineTheme.Text,
            Margin = new(0, 0, 0, 3), Size = new(650, 43), Text = title
        });
        if (!string.IsNullOrWhiteSpace(description))
            content.Controls.Add(new Label
            {
                AutoSize = false, ForeColor = DesktopShrineTheme.MutedText,
                Margin = new(0, 0, 0, 16), Size = new(650, 38), Text = description
            });
        quickCountLabel = new Label
        {
            AutoSize = false,
            ForeColor = DesktopShrineTheme.MutedText,
            Margin = new(0, 0, 0, 4),
            Size = new(680, 25),
            TextAlign = ContentAlignment.MiddleRight
        };
        content.Controls.Add(quickCountLabel);
        UpdateQuickCount();
        content.ResumeLayout();
    }

    private void AddSectionHeading(string text) => content.Controls.Add(new Label
    {
        AutoSize = false, Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
        ForeColor = DesktopShrineTheme.Cyan, Margin = new(0, 12, 0, 5),
        Size = new(680, 31), Text = text
    });

    private void AddSetting(PluginSettingDefinition setting)
    {
        var row = new Panel
        {
            BackColor = DesktopShrineTheme.Surface,
            Margin = new(0, 0, 0, 10),
            Padding = new(12),
            Size = new(680, setting.ControlType is PluginSettingControlType.Slider or PluginSettingControlType.Choice ? 164 : 116)
        };
        Control editor = CreateEditor(setting);
        editor.Location = new(0, 0);
        editor.Width = setting.CanAddToQuickAccess ? 530 : 650;
        row.Controls.Add(editor);

        if (setting.CanAddToQuickAccess)
        {
            var quick = StyledButton("");
            quick.Location = new(542, 18);
            quick.Size = new(122, 38);
            quick.Tag = setting.Id;
            quick.Click += async (_, _) => await ToggleQuickAccessAsync(setting.Id);
            row.Controls.Add(quick);
            quickButtons[setting.Id] = quick;
        }
        if (!string.IsNullOrWhiteSpace(setting.Description) || setting.RequiresRestart)
        {
            var reason = setting.RequiresRestart && !string.IsNullOrWhiteSpace(setting.RestartReason)
                ? $"{setting.Description}  {setting.RestartReason}".Trim()
                : setting.Description;
            var top = row.Height - 38;
            row.Controls.Add(new Label
            {
                AutoEllipsis = true, ForeColor = setting.RequiresRestart
                    ? Color.FromArgb(238, 132, 190) : DesktopShrineTheme.MutedText,
                Location = new(16, top), Size = new(648, 28), Text = reason
            });
        }
        content.Controls.Add(row);
        RefreshQuickButtons();
    }

    private Control CreateEditor(PluginSettingDefinition setting)
    {
        var value = definition.ReadValue(setting);
        if (setting.ControlType == PluginSettingControlType.Slider)
        {
            var sliderDefinition = new TaskbarSliderDefinition(setting.Id,
                setting.DisplayName, setting.PluginId, setting.SettingPath,
                (float)setting.Minimum!.Value, (float)setting.Maximum!.Value,
                (float)setting.Step!.Value,
                string.Equals(setting.Mapping, "logarithmic", StringComparison.OrdinalIgnoreCase)
                    ? TaskbarSliderMapping.Logarithmic : TaskbarSliderMapping.Linear,
                100d,
                string.Equals(setting.Format, "percent", StringComparison.OrdinalIgnoreCase)
                    ? TaskbarSliderValueFormat.Percent : TaskbarSliderValueFormat.DecimalOne);
            var slider = new ModernSliderControl(sliderDefinition, Convert.ToSingle(value, CultureInfo.InvariantCulture));
            slider.ValueChanged += next => _ = CommitAsync(setting, next);
            return slider;
        }
        if (setting.ControlType == PluginSettingControlType.Toggle)
        {
            var toggle = new ModernToggleControl(new(setting.Id, setting.DisplayName,
                setting.PluginId, setting.SettingPath), Convert.ToBoolean(value, CultureInfo.InvariantCulture));
            toggle.ValueChanged += next => _ = CommitAsync(setting, next);
            return toggle;
        }
        if (setting.ControlType == PluginSettingControlType.Choice)
        {
            IReadOnlyList<TaskbarChoice> Choices() => definition.ReadChoices(setting);
            var choice = new ModernChoiceControl(new(setting.Id, setting.DisplayName,
                setting.PluginId, setting.SettingPath, Choices), Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
            choice.ValueChanged += next => _ = CommitAsync(setting, next);
            return choice;
        }
        return CreateTextEditor(setting, value);
    }

    private Control CreateTextEditor(PluginSettingDefinition setting, object value)
    {
        var panel = new Panel { BackColor = DesktopShrineTheme.Surface, Size = new(530, 75) };
        panel.Controls.Add(new Label
        {
            AutoSize = false, ForeColor = DesktopShrineTheme.Text, Location = new(4, 2),
            Size = new(510, 25), Text = setting.DisplayName
        });
        var input = new TextBox
        {
            BackColor = DesktopShrineTheme.Background, BorderStyle = BorderStyle.FixedSingle,
            ForeColor = DesktopShrineTheme.Text, Location = new(4, 32), Size = new(510, 30),
            Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
        async Task SaveAsync()
        {
            object next = input.Text;
            if (setting.ControlType == PluginSettingControlType.Number)
            {
                if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                { SetStatus($"{setting.DisplayName}: enter a valid number.", true); return; }
                if (setting.Minimum is not null) number = Math.Max(number, setting.Minimum.Value);
                if (setting.Maximum is not null) number = Math.Min(number, setting.Maximum.Value);
                next = number;
                input.Text = number.ToString(CultureInfo.InvariantCulture);
            }
            await CommitAsync(setting, next);
        }
        input.Leave += async (_, _) => await SaveAsync();
        input.KeyDown += async (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            args.SuppressKeyPress = true; await SaveAsync();
        };
        panel.Controls.Add(input);
        return panel;
    }

    private async Task CommitAsync(PluginSettingDefinition setting, object value)
    {
        try
        {
            await definition.WriteValue(setting, value, CancellationToken.None);
            SetStatus(setting.RequiresRestart
                ? $"Saved {setting.DisplayName.TrimEnd(' ', '*')}. Restart pending."
                : $"Updated {setting.DisplayName.TrimEnd(' ', '*')}.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not update {setting.DisplayName}: {exception.Message}", true);
            currentPage?.Invoke();
        }
    }

    private async Task ToggleQuickAccessAsync(string id)
    {
        var next = new HashSet<string>(quickAccess, StringComparer.OrdinalIgnoreCase);
        if (!next.Remove(id))
        {
            if (next.Count >= 5) { SetStatus("Quick Access is limited to 5 items.", true); return; }
            next.Add(id);
        }
        try
        {
            await definition.WriteQuickAccess(next.ToArray(), CancellationToken.None);
            quickAccess = next;
            RefreshQuickButtons();
            SetStatus($"Quick Access: {quickAccess.Count}/5 selected.", false);
        }
        catch (Exception exception) { SetStatus($"Could not update Quick Access: {exception.Message}", true); }
    }

    private void RefreshQuickButtons()
    {
        foreach (var (id, button) in quickButtons)
        {
            var selected = quickAccess.Contains(id);
            button.Text = selected ? "Remove quick" : "+ Quick Access";
            button.Enabled = selected || quickAccess.Count < 5;
            button.ForeColor = selected ? DesktopShrineTheme.Magenta : DesktopShrineTheme.Cyan;
        }
        UpdateQuickCount();
    }

    private void UpdateQuickCount()
    {
        if (quickCountLabel is not null)
            quickCountLabel.Text = $"Quick Access  {quickAccess.Count}/5";
    }

    private void AddRestartNote() => content.Controls.Add(new Label
    {
        AutoSize = false, ForeColor = Color.FromArgb(238, 132, 190),
        Margin = new(0, 12, 0, 20), Size = new(680, 44),
        Text = "* This setting requires Desktop Shrine to restart before it takes effect."
    });

    private void ShowApplication()
    {
        currentPage = ShowApplication;
        BeginPage("Application", "Useful Desktop Shrine locations and lifecycle actions.");
        AddSectionHeading("Folders");
        AddFolder("Installation folder", definition.Paths.InstallationDirectory);
        AddFolder("Configuration and data", definition.Paths.DataDirectory);
        AddFolder("Configuration", definition.Paths.ConfigurationDirectory);
        AddFolder("Logs", definition.Paths.LogsDirectory);
        AddFolder("Plugins", definition.Paths.PluginDirectory);
        AddSectionHeading("Desktop Shrine");
        AddAction("Restart Desktop Shrine", () => definition.ShutdownRequested(DesktopShrine.Abstractions.ApplicationShutdownKind.Restart));
        AddAction("Exit Desktop Shrine", () => definition.ShutdownRequested(DesktopShrine.Abstractions.ApplicationShutdownKind.Exit));
    }

    private void AddFolder(string name, string path) => AddAction($"{name}   ›", () =>
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) { SetStatus($"Could not open {name}: {exception.Message}", true); }
    }, path);

    private void AddAction(string name, Action action, string? detail = null)
    {
        var row = new Panel { BackColor = DesktopShrineTheme.Surface, Margin = new(0, 0, 0, 8), Size = new(680, 70) };
        var button = StyledButton(name);
        button.Location = new(12, 10); button.Size = new(650, detail is null ? 48 : 30);
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Click += (_, _) => action();
        row.Controls.Add(button);
        if (detail is not null) row.Controls.Add(new Label
        {
            AutoEllipsis = true, ForeColor = DesktopShrineTheme.MutedText,
            Location = new(24, 42), Size = new(625, 22), Text = detail
        });
        content.Controls.Add(row);
    }

    private static Button StyledButton(string text)
    {
        var button = new Button
        {
            BackColor = DesktopShrineTheme.SurfaceRaised,
            FlatStyle = FlatStyle.Flat,
            ForeColor = DesktopShrineTheme.Text,
            Margin = new(0, 0, 0, 6),
            Text = text,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = DesktopShrineTheme.Hover;
        button.FlatAppearance.MouseDownBackColor = DesktopShrineTheme.Pressed;
        return button;
    }

    private void SetStatus(string text, bool error)
    {
        status.ForeColor = error ? Color.FromArgb(255, 116, 151) : DesktopShrineTheme.Cyan;
        status.Text = text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { logo.Dispose(); Icon?.Dispose(); }
        base.Dispose(disposing);
    }
}
