using DesktopShrine.Abstractions;
using DesktopShrine.Plugin.TaskbarControls;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class TaskbarControlsTests
{
    [Fact]
    public void ProductVersionIdentifiesTheTwoPointZeroAlphaBuild()
    {
        Assert.Equal("2.0 Alpha", DesktopShrineProductInfo.VersionDisplay);
    }

    [Fact]
    public void CustomTitleBarStartsNativeCaptionDrag()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var owner = new RecordingCaptionForm();
                using var titleBar = new SettingsTitleBar(owner);
                _ = owner.Handle;
                var onMouseDown = typeof(SettingsTitleBar).GetMethod(
                    "OnMouseDown",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("OnMouseDown");

                onMouseDown.Invoke(titleBar,
                [
                    new MouseEventArgs(
                        MouseButtons.Left,
                        clicks: 1,
                        x: 20,
                        y: 20,
                        delta: 0)
                ]);

                Assert.Equal(2, owner.LastNonClientHit);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) throw failure;
    }

    [Fact]
    public void PluginSettingsMetadataIsDiscoveredAndBoundToOwningPlugin()
    {
        var root = Directory.CreateTempSubdirectory("desktop-shrine-settings-");
        try
        {
            var plugin = Directory.CreateDirectory(Path.Combine(root.FullName, "example"));
            File.WriteAllText(Path.Combine(plugin.FullName, "settings.json"), """
                {
                  "pluginId": "example",
                  "displayName": "Example",
                  "settings": [
                    { "id": "example.level", "settingPath": "Level", "displayName": "Level", "controlType": "Slider", "defaultValue": "0.5", "minimum": 0, "maximum": 1, "step": 0.1, "canAddToQuickAccess": true }
                  ]
                }
                """);

            var groups = PluginSettingsCatalog.Discover(root.FullName);

            var group = Assert.Single(groups);
            var setting = Assert.Single(group.Settings);
            Assert.Equal("example", setting.PluginId);
            Assert.Equal("example.level", setting.Id);
            Assert.True(setting.CanAddToQuickAccess);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void QuickAccessUsesStableIdsAndCapsSelectionAtFive()
    {
        var selected = PluginSettingsCatalog.ParseQuickAccess(
            "one;two;three;four;five;six;two");

        Assert.Equal(["one", "two", "three", "four", "five"], selected);
        Assert.Equal("one;two;three;four;five",
            PluginSettingsCatalog.SerialiseQuickAccess(selected.Append("six")));
    }

    [Fact]
    public void PathDefaultsExpandToTheirEffectiveAbsoluteLocation()
    {
        var setting = new PluginSettingDefinition
        {
            Id = "example.path", PluginId = "example",
            PluginDisplayName = "Example", SettingPath = "Path",
            DisplayName = "Path", ControlType = PluginSettingControlType.Path,
            DefaultValue = "%LOCALAPPDATA%\\DesktopShrine\\cache"
        };

        var value = Assert.IsType<string>(
            PluginSettingsCatalog.ParseValue(setting, null));

        Assert.True(Path.IsPathFullyQualified(value));
        Assert.EndsWith(Path.Combine("DesktopShrine", "cache"), value);
    }

    [Fact]
    public void SettingsWindowUsesBrandedLayoutAndRendersPluginNavigation()
    {
        Exception? failure = null;
        Bitmap? rendered = null;
        var thread = new Thread(() =>
        {
            try
            {
                var setting = new PluginSettingDefinition
                {
                    Id = "example.level", PluginId = "example",
                    PluginDisplayName = "Example", SettingPath = "Level",
                    DisplayName = "Level", Description = "Example setting",
                    ControlType = PluginSettingControlType.Slider,
                    DefaultValue = "0.5", Minimum = 0, Maximum = 1, Step = 0.01,
                    Format = "percent", CanAddToQuickAccess = true
                };
                var overflowSettings = Enumerable.Range(0, 10)
                    .Select(index => setting with
                    {
                        Id = $"example.level-{index}",
                        DisplayName = $"Level {index + 1}"
                    })
                    .ToArray();
                var model = new DesktopShrineSettingsDefinition(
                    [new("example", "Example", "Example plugin", overflowSettings)],
                    _ => 0.5d,
                    _ => [],
                    (_, _, _) => ValueTask.CompletedTask,
                    () => ["example.level"],
                    (_, _) => ValueTask.CompletedTask,
                    new("C:\\app", "C:\\data", "C:\\config", "C:\\logs", "C:\\plugins"),
                    _ => { });
                using var window = new DesktopShrineSettingsWindow(
                    WindowsTaskbarIcon.LoadProductImage(), model)
                {
                    Location = new(-10000, -10000)
                };
                window.Show();
                Application.DoEvents();
                rendered = new(window.ClientSize.Width, window.ClientSize.Height);
                window.DrawToBitmap(rendered, window.ClientRectangle);
                var snapshot = Environment.GetEnvironmentVariable(
                    "DESKTOP_SHRINE_SETTINGS_SNAPSHOT");
                if (!string.IsNullOrWhiteSpace(snapshot))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
                    rendered.Save(snapshot);
                }
                window.CloseForApplication();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) throw failure;
        using var image = Assert.IsType<Bitmap>(rendered);
        Assert.Equal(980, image.Width);
        Assert.Equal(720, image.Height);
        Assert.Contains(Enumerable.Range(0, image.Width)
            .SelectMany(x => Enumerable.Range(0, image.Height)
                .Select(y => image.GetPixel(x, y))),
            colour => colour.G > 170 && colour.B > 180 && colour.R < 80);
    }

    [Fact]
    public void BrandedPanelScrollsWithoutUsingAutoScrollPosition()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new Form { ClientSize = new(320, 240) };
                using var panel = new BrandedFlowLayoutPanel
                {
                    AutoScroll = true,
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false
                };
                panel.Controls.Add(new Panel { Size = new(280, 900) });
                window.Controls.Add(panel);
                window.Show();
                Application.DoEvents();

                Assert.True(panel.BrandedScrollRange > 0);
                panel.SetBrandedScrollPosition(180);
                Assert.Equal(180, panel.BrandedScrollPosition);
                panel.SetBrandedScrollPosition(int.MaxValue);
                Assert.Equal(
                    panel.BrandedScrollRange,
                    panel.BrandedScrollPosition);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) throw failure;
    }

    [Fact]
    public void ProductIconIsEmbeddedForTheNotificationArea()
    {
        using var icon = WindowsTaskbarIcon.LoadProductIcon();

        Assert.True(icon.Width > 0);
        Assert.True(icon.Height > 0);
    }

    [Fact]
    public void ExistingToriiLogoIsEmbeddedForThePopupHeader()
    {
        using var logo = WindowsTaskbarIcon.LoadProductImage();

        Assert.True(logo.Width > 0);
        Assert.True(logo.Height > 0);
    }

    [Fact]
    public void DefaultsDescribeBrightnessGammaAndAudioControls()
    {
        var settings = TaskbarControlsSettings.FromConfiguration(
            new ConfigurationBuilder().Build());

        Assert.Equal("BlinkStick brightness", settings.Brightness.DisplayName);
        Assert.Equal("Brightness", settings.Brightness.SettingPath);
        Assert.Equal(0.01f, settings.Brightness.Step);
        Assert.Equal(TaskbarSliderMapping.Logarithmic, settings.Brightness.Mapping);
        Assert.Equal(100d, settings.Brightness.LogarithmicRange);
        Assert.Equal("BlinkStick gamma", settings.Gamma.DisplayName);
        Assert.Equal("Gamma", settings.Gamma.SettingPath);
        Assert.Equal(0.1f, settings.Gamma.Step);
        Assert.Equal(0.1f, settings.Gamma.Minimum);
        Assert.Equal(4f, settings.Gamma.Maximum);
        Assert.Equal("Audio source", settings.AudioDevice.DisplayName);
        Assert.Equal("audio-collector", settings.AudioDevice.TargetPluginId);
        Assert.Equal("Device", settings.AudioDevice.SettingPath);
        Assert.True(TaskbarControlsSettings.Validate(settings).IsValid);
    }

    [Fact]
    public void LegacyListStepDoesNotReduceSliderPrecision()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BlinkStickBrightness:StepPercent"] = "10"
            })
            .Build();

        var settings = TaskbarControlsSettings.FromConfiguration(configuration);

        Assert.Equal(0.01f, settings.Brightness.Step);
    }

    [Fact]
    public void AudioSelectorIncludesEveryActiveEndpointUsingStableIds()
    {
        var catalog = new RecordingAudioEndpointCatalog(new(
            new("default-id", "Speakers"),
            [
                new("endpoint-2", "Headphones"),
                new("endpoint-1", "Speakers"),
                new("endpoint-2", "Duplicate headphones")
            ]));
        var provider = new WindowsAudioDeviceProvider(catalog);

        var choices = provider.GetDevices("loopback");

        Assert.False(catalog.CaptureInput);
        Assert.Collection(
            choices,
            choice => Assert.Equal("default", choice.Value),
            choice => Assert.Equal("endpoint-2", choice.Value),
            choice => Assert.Equal("endpoint-1", choice.Value));
        Assert.Equal("Default — Speakers", choices[0].DisplayName);
    }

    [Fact]
    public void InputCaptureModeEnumeratesCaptureEndpoints()
    {
        var catalog = new RecordingAudioEndpointCatalog(new(
            null,
            [new("microphone-id", "Microphone")]));
        var provider = new WindowsAudioDeviceProvider(catalog);

        var choices = provider.GetDevices("input");

        Assert.True(catalog.CaptureInput);
        Assert.Equal("default", choices[0].Value);
        Assert.Equal("microphone-id", choices[1].Value);
    }

    [Fact]
    public void AudioEndpointEnumerationRunsInMtaAwayFromTrayUiThread()
    {
        ApartmentState? enumerationApartment = null;
        var catalog = new DelegatingAudioEndpointCatalog(captureInput =>
        {
            enumerationApartment = Thread.CurrentThread.GetApartmentState();
            return new(null,
            [
                new("speakers", "Speakers"),
                new("headset", "Headset")
            ]);
        });
        IReadOnlyList<TaskbarChoice>? choices = null;
        var thread = new Thread(() =>
            choices = new WindowsAudioDeviceProvider(catalog)
                .GetDevices("loopback"));
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(ApartmentState.MTA, enumerationApartment);
        Assert.Equal(3, choices!.Count);
    }

    [Fact]
    public void AudioEndpointEnumerationRetriesOnTrayThreadWhenMtaDispatchFails()
    {
        var trayThreadId = 0;
        var enumerationThreadId = 0;
        IReadOnlyList<TaskbarChoice>? choices = null;
        var catalog = new DelegatingAudioEndpointCatalog(_ =>
        {
            enumerationThreadId = Environment.CurrentManagedThreadId;
            return new(null, [new("endpoint", "Speakers")]);
        });
        var provider = new WindowsAudioDeviceProvider(
            catalog,
            new ThrowingAudioEndpointDispatcher());
        var thread = new Thread(() =>
        {
            trayThreadId = Environment.CurrentManagedThreadId;
            choices = provider.GetDevices("loopback");
        });
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(trayThreadId, enumerationThreadId);
        Assert.Equal(2, choices!.Count);
        Assert.Equal("endpoint", choices[1].Value);
    }

    [Fact]
    public void EmptyMtaSnapshotRetriesOnTrayThreadBeforeShowingOnlyDefault()
    {
        var enumerations = 0;
        var catalog = new DelegatingAudioEndpointCatalog(_ =>
        {
            enumerations++;
            return new(null, [new("endpoint", "Speakers")]);
        });
        var provider = new WindowsAudioDeviceProvider(
            catalog,
            new EmptyAudioEndpointDispatcher());

        var choices = provider.GetDevices("loopback");

        Assert.Equal(1, enumerations);
        Assert.Equal(2, choices.Count);
        Assert.Equal("endpoint", choices[1].Value);
    }

    [Fact]
    public void SparseMtaSnapshotMergesDevicesFoundByTrayThreadRetry()
    {
        var catalog = new DelegatingAudioEndpointCatalog(_ => new(
            new("default-id", "Speakers"),
            [
                new("default-id", "Speakers"),
                new("headset-id", "Headset")
            ]));
        var provider = new WindowsAudioDeviceProvider(
            catalog,
            new SparseAudioEndpointDispatcher());

        var choices = provider.GetDevices("loopback");

        Assert.Equal(3, choices.Count);
        Assert.Contains(choices, choice => choice.Value == "default-id");
        Assert.Contains(choices, choice => choice.Value == "headset-id");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.01f)]
    [InlineData(0.10f)]
    [InlineData(0.50f)]
    [InlineData(0.90f)]
    [InlineData(1f)]
    public void LogarithmicSliderRoundTripsOnePercentValues(float brightness)
    {
        var slider = CreateBrightnessDefinition();
        var position = slider.PositionFromValue(
            brightness,
            ModernSliderControl.SliderMaximum);
        var restored = slider.ValueFromPosition(
            position,
            ModernSliderControl.SliderMaximum);

        Assert.Equal(brightness, restored, precision: 2);
    }

    [Fact]
    public void LogarithmicSliderAllocatesMoreTravelToLowBrightness()
    {
        var slider = CreateBrightnessDefinition();
        var halfTravel = slider.ValueFromPosition(
            ModernSliderControl.SliderMaximum / 2,
            ModernSliderControl.SliderMaximum);

        Assert.Equal(0.09f, halfTravel, precision: 2);
    }

    [Fact]
    public void GammaSliderUsesLinearTenths()
    {
        var settings = TaskbarControlsSettings.FromConfiguration(
            new ConfigurationBuilder().Build()).Gamma;
        var slider = CreateDefinition(settings);

        Assert.Equal(2.3f, slider.Quantise(2.26f));
        Assert.Equal(0.1f, slider.ValueFromPosition(
            0,
            ModernSliderControl.SliderMaximum));
        Assert.Equal(4f, slider.ValueFromPosition(
            ModernSliderControl.SliderMaximum,
            ModernSliderControl.SliderMaximum));
    }

    [Fact]
    public void BrandedTrayPopupRendersCompleteMockupStructure()
    {
        Bitmap? rendered = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var settings = TaskbarControlsSettings.FromConfiguration(
                    new ConfigurationBuilder().Build());
                using var brightness = new ModernSliderControl(
                    CreateDefinition(settings.Brightness),
                    0.01f);
                using var gamma = new ModernSliderControl(
                    CreateDefinition(settings.Gamma),
                    2.2f);
                var deviceDefinition = new TaskbarChoiceDefinition(
                    "audio-device",
                    "Audio source",
                    "audio-collector",
                    "Device",
                    () =>
                    [
                        new("default", "Default — Speakers"),
                        new("headphones", "Headphones")
                    ]);
                using var device = new ModernChoiceControl(
                    deviceDefinition,
                    "default");
                using var popup = new DesktopShrineTrayPopup(
                    WindowsTaskbarIcon.LoadProductImage());
                popup.AddControl(brightness);
                popup.AddControl(gamma);
                popup.AddControl(device);
                popup.AddCommand(new TrayActionRow(
                    "Restart Desktop Shrine",
                    ShrineGlyph.Restart));
                popup.AddCommand(new TrayActionRow(
                    "Exit Desktop Shrine",
                    ShrineGlyph.Power));
                popup.Location = new(-10000, -10000);
                popup.Show();
                Application.DoEvents();
                rendered = new(popup.Width, popup.Height);
                popup.DrawToBitmap(rendered, popup.ClientRectangle);
                popup.Hide();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;

        using (var image = Assert.IsType<Bitmap>(rendered))
        {
            Assert.Equal(420, image.Width);
            Assert.InRange(image.Height, 560, 600);
            Assert.Contains(
                Enumerable.Range(0, image.Width)
                    .SelectMany(x => Enumerable.Range(0, image.Height)
                        .Select(y => image.GetPixel(x, y))),
                colour => colour.B > 200
                    && colour.G > 150
                    && colour.R < 100);
            var output = Environment.GetEnvironmentVariable(
                "DESKTOP_SHRINE_TRAY_SNAPSHOT");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                image.Save(output);
            }
        }
    }

    [Fact]
    public void AudioSelectorOpensStyledFlyoutWithoutEndingUiThread()
    {
        Exception? failure = null;
        var opened = false;
        var thread = new Thread(() =>
        {
            try
            {
                var snapshot = Environment.GetEnvironmentVariable(
                    "DESKTOP_SHRINE_AUDIO_FLYOUT_SNAPSHOT");
                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    Location = string.IsNullOrWhiteSpace(snapshot)
                        ? new Point(-10000, -10000)
                        : new Point(80, 80),
                    Size = new(420, 200),
                    ShowInTaskbar = false
                };
                using var control = new ModernChoiceControl(
                    new TaskbarChoiceDefinition(
                        "audio-device",
                        "Audio source",
                        "audio-collector",
                        "Device",
                        () =>
                        [
                            new("default", "Default — Speakers"),
                            new("headphones", "Headphones")
                        ]),
                    "default");
                form.Controls.Add(control);
                form.Show();
                Application.DoEvents();
                control.OpenDropDownForTesting();
                Application.DoEvents();
                opened = control.IsDropDownOpen;
                if (!string.IsNullOrWhiteSpace(snapshot))
                {
                    var flyout = control.FlyoutForTesting
                        ?? throw new InvalidOperationException("Audio flyout was not created.");
                    using var image = new Bitmap(flyout.Width, flyout.Height);
                    flyout.DrawToBitmap(image, flyout.ClientRectangle);
                    Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
                    image.Save(snapshot);
                }
                form.Close();
                Application.DoEvents();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
            throw failure;
        Assert.True(opened);
    }

    [Fact]
    public void PointerHighlightClearsIndependentlyFromFocusAndSelection()
    {
        using var action = new TrayActionRow(
            "Restart Desktop Shrine",
            ShrineGlyph.Restart);
        InvokePointerMethod(action, "OnMouseEnter", EventArgs.Empty);
        Assert.True(action.IsPointerHighlightVisible);
        InvokePointerMethod(action, "OnMouseLeave", EventArgs.Empty);
        Assert.False(action.IsPointerHighlightVisible);

        using var choice = new ModernChoiceControl(
            new TaskbarChoiceDefinition(
                "audio-device",
                "Audio source",
                "audio-collector",
                "Device",
                () => [new("default", "Default device")]),
            "default");
        InvokePointerMethod(
            choice,
            "OnMouseMove",
            new MouseEventArgs(MouseButtons.None, 0, 100, 75, 0));
        Assert.True(choice.IsPointerHighlightVisible);
        InvokePointerMethod(choice, "OnMouseLeave", EventArgs.Empty);
        Assert.False(choice.IsPointerHighlightVisible);
    }

    [Fact]
    public void PopupPositioningStaysInsideTheCurrentScreenWorkingArea()
    {
        Exception? failure = null;
        var insideWorkingArea = false;
        var thread = new Thread(() =>
        {
            try
            {
                using var popup = new DesktopShrineTrayPopup(
                    WindowsTaskbarIcon.LoadProductImage());
                var screen = Screen.PrimaryScreen
                    ?? throw new InvalidOperationException("No primary screen is available.");
                var anchor = new Point(
                    screen.WorkingArea.Right - 2,
                    screen.WorkingArea.Bottom - 2);
                popup.ShowAt(anchor);
                Application.DoEvents();
                insideWorkingArea = screen.WorkingArea.Contains(popup.Bounds);
                popup.Hide();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
            throw failure;
        Assert.True(insideWorkingArea);
    }

    [Fact]
    public async Task MenuChangesPersistToTheirTargetPlugins()
    {
        var editor = new RecordingConfigurationEditor(new Dictionary<string, string>
        {
            ["blinkstick-bar:Brightness"] = "0.9",
            ["blinkstick-bar:Gamma"] = "2.2",
            ["audio-collector:Device"] = "default",
            ["audio-collector:CaptureMode"] = "loopback"
        });
        var devices = new RecordingAudioDeviceProvider();
        var icon = new RecordingTaskbarIcon();
        var application = new RecordingApplicationControl();
        var plugin = new TaskbarControlsPlugin(() => icon, devices);
        var context = new TestPluginContext(editor, application);
        try
        {
            await plugin.InitialiseAsync(
                context,
                TestContext.Current.CancellationToken);
            await plugin.StartAsync(TestContext.Current.CancellationToken);

            Assert.Collection(
                icon.Menu!.Controls,
                control => Assert.Equal("blinkstick.brightness", control.Id),
                control => Assert.Equal("blinkstick.gamma", control.Id),
                control => Assert.Equal("audio.device", control.Id));
            Assert.Collection(
                icon.Menu.Commands,
                command =>
                {
                    Assert.Equal("Open Desktop Shrine / Settings", command.DisplayName);
                    Assert.Equal(TaskbarCommandKind.OpenSettings, command.Kind);
                },
                command =>
                {
                    Assert.Equal("Restart Desktop Shrine", command.DisplayName);
                    Assert.Equal(TaskbarCommandKind.Restart, command.Kind);
                },
                command =>
                {
                    Assert.Equal("Exit Desktop Shrine", command.DisplayName);
                    Assert.Equal(TaskbarCommandKind.Exit, command.Kind);
                });
            Assert.Equal(0.9d, icon.Values["blinkstick.brightness"]);
            Assert.Equal(2.2d, icon.Values["blinkstick.gamma"]);

            var audio = Assert.IsType<TaskbarChoiceDefinition>(
                icon.Menu.Controls[2]);
            Assert.Equal(2, audio.GetChoices().Count);
            Assert.Equal("loopback", devices.CaptureMode);
            Assert.Equal(1, devices.CallCount);
            audio.GetChoices();
            Assert.Equal(1, devices.CallCount);

            await icon.ChangeAsync(
                "blinkstick.brightness",
                0.41f,
                TestContext.Current.CancellationToken);
            await icon.ChangeAsync(
                "blinkstick.gamma",
                1.8f,
                TestContext.Current.CancellationToken);
            await icon.ChangeAsync(
                "audio.device",
                "endpoint-2",
                TestContext.Current.CancellationToken);

            Assert.Contains(editor.Writes, write =>
                write is ("blinkstick-bar", "Brightness", float value)
                && value == 0.41f);
            Assert.Contains(editor.Writes, write =>
                write is ("blinkstick-bar", "Gamma", float value)
                && value == 1.8f);
            Assert.Contains(editor.Writes, write =>
                write is ("audio-collector", "Device", string value)
                && value == "endpoint-2");
            icon.RequestShutdown(ApplicationShutdownKind.Restart);
            Assert.Equal(ApplicationShutdownKind.Restart, application.Requested);
            Assert.Equal("endpoint-2", icon.Values["audio.device"]);
        }
        finally
        {
            await plugin.StopAsync(CancellationToken.None);
            await plugin.DisposeAsync();
        }
    }

    private static void InvokePointerMethod(
        Control control,
        string methodName,
        EventArgs eventArgs)
    {
        var method = control.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Could not find {methodName} on {control.GetType().Name}.");
        method.Invoke(control, [eventArgs]);
    }

    private static TaskbarSliderDefinition CreateBrightnessDefinition()
    {
        var settings = TaskbarControlsSettings.FromConfiguration(
            new ConfigurationBuilder().Build()).Brightness;
        return CreateDefinition(settings);
    }

    private static TaskbarSliderDefinition CreateDefinition(
        TaskbarSliderSettings settings) => new(
            settings.Id,
            settings.DisplayName,
            settings.TargetPluginId,
            settings.SettingPath,
            settings.Minimum,
            settings.Maximum,
            settings.Step,
            settings.Mapping,
            settings.LogarithmicRange,
            settings.Format);

    private sealed class RecordingConfigurationEditor(
        IReadOnlyDictionary<string, string> values) : IPluginConfigurationEditor
    {
        public List<(string PluginId, string SettingPath, object? Value)> Writes
        {
            get;
        } = [];

        public string? GetValue(string pluginId, string settingPath) =>
            values.TryGetValue($"{pluginId}:{settingPath}", out var value)
                ? value
                : null;

        public ValueTask SetValueAsync(
            string pluginId,
            string settingPath,
            object? value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add((pluginId, settingPath, value));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAudioDeviceProvider : IAudioDeviceProvider
    {
        public string? CaptureMode { get; private set; }
        public int CallCount { get; private set; }

        public IReadOnlyList<TaskbarChoice> GetDevices(string captureMode)
        {
            CallCount++;
            CaptureMode = captureMode;
            return
            [
                new("default", "Default — Speakers"),
                new("endpoint-2", "Headphones")
            ];
        }
    }

    private sealed class RecordingAudioEndpointCatalog(
        AudioEndpointSnapshot snapshot) : IAudioEndpointCatalog
    {
        public bool CaptureInput { get; private set; }

        public AudioEndpointSnapshot Enumerate(bool captureInput)
        {
            CaptureInput = captureInput;
            return snapshot;
        }
    }

    private sealed class DelegatingAudioEndpointCatalog(
        Func<bool, AudioEndpointSnapshot> enumerate) : IAudioEndpointCatalog
    {
        public AudioEndpointSnapshot Enumerate(bool captureInput) =>
            enumerate(captureInput);
    }

    private sealed class ThrowingAudioEndpointDispatcher :
        IAudioEndpointDispatcher
    {
        public AudioEndpointSnapshot Enumerate(
            IAudioEndpointCatalog catalog,
            bool captureInput) => throw new InvalidOperationException(
                "Simulated MTA dispatch failure.");
    }

    private sealed class EmptyAudioEndpointDispatcher :
        IAudioEndpointDispatcher
    {
        public AudioEndpointSnapshot Enumerate(
            IAudioEndpointCatalog catalog,
            bool captureInput) => new(null, []);
    }

    private sealed class SparseAudioEndpointDispatcher :
        IAudioEndpointDispatcher
    {
        public AudioEndpointSnapshot Enumerate(
            IAudioEndpointCatalog catalog,
            bool captureInput) => new(
                new("default-id", "Speakers"),
                [new("default-id", "Speakers")]);
    }

    private sealed class RecordingTaskbarIcon : ITaskbarIcon
    {
        private Func<TaskbarControlChange, CancellationToken, ValueTask>? callback;
        private Action<ApplicationShutdownKind>? shutdown;

        public TaskbarMenuDefinition? Menu { get; private set; }
        public Dictionary<string, object> Values { get; } = [];

        public ValueTask StartAsync(
            TaskbarMenuDefinition menu,
            IReadOnlyDictionary<string, object> currentValues,
            Func<TaskbarControlChange, CancellationToken, ValueTask> valueChanged,
            DesktopShrineSettingsDefinition settings,
            Action<ApplicationShutdownKind> shutdownRequested,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Menu = menu;
            foreach (var pair in currentValues)
                Values[pair.Key] = pair.Value;
            callback = valueChanged;
            shutdown = shutdownRequested;
            return ValueTask.CompletedTask;
        }

        public void UpdateValue(string controlId, object value) =>
            Values[controlId] = value;

        public void UpdateMenu(
            TaskbarMenuDefinition menu,
            IReadOnlyDictionary<string, object> currentValues)
        {
            Menu = menu;
            Values.Clear();
            foreach (var pair in currentValues)
                Values[pair.Key] = pair.Value;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask ChangeAsync(
            string controlId,
            object value,
            CancellationToken cancellationToken)
        {
            var control = Menu!.Controls.Single(item => item.Id == controlId);
            return callback!(new(control, value), cancellationToken);
        }

        public void RequestShutdown(ApplicationShutdownKind kind) =>
            shutdown!(kind);
    }

    private sealed class TestPluginContext(
        IPluginConfigurationEditor editor,
        IApplicationControl applicationControl) : IPluginContext
    {
        public string PluginId => "taskbar-controls";
        public IConfiguration Configuration { get; } =
            new ConfigurationBuilder().Build();
        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
        public IPluginPublisher Publisher => throw new NotSupportedException();
        public IPluginSubscriber Subscriber => throw new NotSupportedException();
        public ILiveConfiguration<OutputInputProfile>? InputProfile => null;
        public IPluginConfigurationEditor? ConfigurationEditor => editor;
        public IApplicationControl? ApplicationControl => applicationControl;

        public ILiveConfiguration<TConfig> ObserveConfiguration<TConfig>(
            Func<IConfiguration, TConfig> snapshotFactory,
            Func<TConfig, ConfigurationValidationResult>? validator = null)
            where TConfig : notnull => throw new NotSupportedException();
    }

    private sealed class RecordingApplicationControl : IApplicationControl
    {
        public ApplicationShutdownKind? Requested { get; private set; }

        public void RequestShutdown(ApplicationShutdownKind kind) =>
            Requested = kind;
    }

    private sealed class RecordingCaptionForm : Form
    {
        public int? LastNonClientHit { get; private set; }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x00A1)
            {
                LastNonClientHit = message.WParam.ToInt32();
                message.Result = 0;
                return;
            }
            base.WndProc(ref message);
        }
    }
}
