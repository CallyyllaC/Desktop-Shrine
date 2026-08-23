using System.Drawing;
using System.Windows.Forms;
using DesktopShrine.Abstractions;

namespace DesktopShrine.Plugin.TaskbarControls;

internal interface ITaskbarIcon : IAsyncDisposable
{
    ValueTask StartAsync(
        TaskbarMenuDefinition menu,
        IReadOnlyDictionary<string, object> currentValues,
        Func<TaskbarControlChange, CancellationToken, ValueTask> valueChanged,
        DesktopShrineSettingsDefinition settings,
        Action<ApplicationShutdownKind> shutdownRequested,
        CancellationToken cancellationToken);

    void UpdateValue(string controlId, object value);
    void UpdateMenu(
        TaskbarMenuDefinition menu,
        IReadOnlyDictionary<string, object> currentValues);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsTaskbarIcon : ITaskbarIcon
{
    private readonly object gate = new();
    private readonly TaskCompletionSource started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, object> currentValues = [];
    private readonly Dictionary<string, ModernSliderControl> sliders = [];
    private readonly Dictionary<string, ModernChoiceControl> choices = [];
    private readonly Dictionary<string, ModernToggleControl> toggles = [];
    private readonly Dictionary<string, CancellationTokenSource> pendingChanges = [];
    private Thread? thread;
    private Control? dispatcher;
    private ApplicationContext? applicationContext;
    private NotifyIcon? notifyIcon;
    private DesktopShrineTrayPopup? popup;
    private DesktopShrineSettingsWindow? settingsWindow;
    private Func<TaskbarControlChange, CancellationToken, ValueTask>? valueChanged;
    private Action<ApplicationShutdownKind>? shutdownRequested;

    public async ValueTask StartAsync(
        TaskbarMenuDefinition menu,
        IReadOnlyDictionary<string, object> values,
        Func<TaskbarControlChange, CancellationToken, ValueTask> changed,
        DesktopShrineSettingsDefinition settings,
        Action<ApplicationShutdownKind> shutdown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(shutdown);
        lock (gate)
        {
            if (thread is not null)
                throw new InvalidOperationException(
                    "The Desktop Shrine taskbar icon is already running.");
            foreach (var pair in values)
                currentValues[pair.Key] = pair.Value;
            valueChanged = changed;
            shutdownRequested = shutdown;
            thread = new(() => Run(menu, settings))
            {
                IsBackground = true,
                Name = "Desktop Shrine taskbar icon"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        await started.Task.WaitAsync(cancellationToken);
    }

    public void UpdateValue(string controlId, object value)
    {
        lock (gate)
            currentValues[controlId] = value;
        Post(() => RefreshControl(controlId));
    }

    public void UpdateMenu(
        TaskbarMenuDefinition menu,
        IReadOnlyDictionary<string, object> values)
    {
        lock (gate)
        {
            currentValues.Clear();
            foreach (var pair in values)
                currentValues[pair.Key] = pair.Value;
        }
        Post(() => RebuildPopup(menu));
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Control? currentDispatcher;
        ApplicationContext? currentContext;
        lock (gate)
        {
            currentDispatcher = dispatcher;
            currentContext = applicationContext;
            if (thread is null)
                return;
        }

        if (currentDispatcher is null || currentContext is null)
        {
            try
            {
                await started.Task.WaitAsync(cancellationToken);
            }
            catch when (stopped.Task.IsCompleted)
            {
                await stopped.Task.WaitAsync(cancellationToken);
                return;
            }
            lock (gate)
            {
                currentDispatcher = dispatcher;
                currentContext = applicationContext;
            }
        }

        if (currentDispatcher is not null && currentContext is not null)
        {
            try
            {
                currentDispatcher.BeginInvoke(currentContext.ExitThread);
            }
            catch (InvalidOperationException)
            {
            }
        }

        await stopped.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    private void Run(
        TaskbarMenuDefinition definition,
        DesktopShrineSettingsDefinition settings)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var currentDispatcher = new Control();
            currentDispatcher.CreateControl();
            using var productIcon = LoadProductIcon();
            var trayPopup = CreatePopup(definition);
            var settingsForm = new DesktopShrineSettingsWindow(
                LoadProductImage(), settings);
            var icon = new NotifyIcon
            {
                Icon = productIcon,
                Text = "Desktop Shrine",
                Visible = true
            };
            icon.MouseUp += (_, eventArgs) =>
            {
                if (eventArgs.Button == MouseButtons.Right)
                    popup?.ToggleAt(Cursor.Position);
            };
            icon.MouseDoubleClick += (_, eventArgs) =>
            {
                if (eventArgs.Button == MouseButtons.Left)
                    settingsWindow?.ShowAndActivate();
            };
            using var context = new ApplicationContext();
            lock (gate)
            {
                dispatcher = currentDispatcher;
                applicationContext = context;
                notifyIcon = icon;
                popup = trayPopup;
                settingsWindow = settingsForm;
            }
            RefreshUi(refreshChoices: false);
            started.TrySetResult();
            Application.Run(context);
        }
        catch (Exception exception)
        {
            started.TrySetException(exception);
        }
        finally
        {
            NotifyIcon? iconToDispose;
            DesktopShrineTrayPopup? popupToDispose;
            DesktopShrineSettingsWindow? settingsToDispose;
            lock (gate)
            {
                iconToDispose = notifyIcon;
                popupToDispose = popup;
                settingsToDispose = settingsWindow;
                notifyIcon = null;
                popup = null;
                settingsWindow = null;
                foreach (var cancellation in pendingChanges.Values)
                    cancellation.Cancel();
                pendingChanges.Clear();
                sliders.Clear();
                choices.Clear();
                toggles.Clear();
                dispatcher = null;
                applicationContext = null;
                shutdownRequested = null;
            }
            if (settingsToDispose is not null)
            {
                settingsToDispose.CloseForApplication();
                settingsToDispose.Dispose();
            }
            popupToDispose?.Dispose();
            if (iconToDispose is not null)
            {
                try
                {
                    iconToDispose.Visible = false;
                }
                catch (ObjectDisposedException)
                {
                }
                iconToDispose.Dispose();
            }
            stopped.TrySetResult();
        }
    }

    private DesktopShrineTrayPopup CreatePopup(TaskbarMenuDefinition definition)
    {
        var result = new DesktopShrineTrayPopup(LoadProductImage());
        BuildControls(result, definition);
        result.Opening += () => RefreshUi(refreshChoices: true);
        return result;
    }

    private void RebuildPopup(TaskbarMenuDefinition definition)
    {
        foreach (var cancellation in pendingChanges.Values)
            cancellation.Cancel();
        pendingChanges.Clear();
        sliders.Clear();
        choices.Clear();
        toggles.Clear();
        var previous = popup;
        var replacement = CreatePopup(definition);
        lock (gate)
            popup = replacement;
        previous?.Hide();
        previous?.Dispose();
    }

    private void BuildControls(
        DesktopShrineTrayPopup popup,
        TaskbarMenuDefinition definition)
    {
        foreach (var control in definition.Controls)
        {
            switch (control)
            {
                case TaskbarSliderDefinition slider:
                {
                    var view = new ModernSliderControl(
                        slider,
                        ReadFloatValue(slider.Id, slider.Minimum));
                    view.ValueChanged += value =>
                        QueueSliderChange(slider, value);
                    sliders[slider.Id] = view;
                    popup.AddControl(view);
                    break;
                }
                case TaskbarChoiceDefinition choice:
                {
                    var view = new ModernChoiceControl(
                        choice,
                        ReadStringValue(choice.Id, "default"));
                    view.ValueChanged += value =>
                        ApplyChoiceChange(choice, value);
                    choices[choice.Id] = view;
                    popup.AddControl(view);
                    break;
                }
                case TaskbarToggleDefinition toggle:
                {
                    var view = new ModernToggleControl(
                        toggle,
                        ReadBoolValue(toggle.Id, false));
                    view.ValueChanged += value => ApplyToggleChange(toggle, value);
                    toggles[toggle.Id] = view;
                    popup.AddControl(view);
                    break;
                }
            }
        }
        foreach (var command in definition.Commands)
        {
            var item = new TrayActionRow(command.DisplayName, command.Kind switch
            {
                TaskbarCommandKind.Restart => ShrineGlyph.Restart,
                TaskbarCommandKind.Exit => ShrineGlyph.Power,
                _ => ShrineGlyph.Gamma
            });
            item.Click += (_, _) =>
            {
                if (command.Kind == TaskbarCommandKind.OpenSettings)
                {
                    popup.Hide();
                    settingsWindow?.ShowAndActivate();
                }
                else
                    shutdownRequested?.Invoke(command.Kind == TaskbarCommandKind.Restart
                        ? ApplicationShutdownKind.Restart : ApplicationShutdownKind.Exit);
            };
            popup.AddCommand(item);
        }
    }

    internal static Icon LoadProductIcon()
    {
        using var stream = typeof(WindowsTaskbarIcon).Assembly
            .GetManifestResourceStream("DesktopShrine.ico")
            ?? throw new InvalidOperationException(
                "The embedded Desktop Shrine icon is unavailable.");
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }

    internal static Image LoadProductImage()
    {
        using var stream = typeof(WindowsTaskbarIcon).Assembly
            .GetManifestResourceStream("DesktopShrine.png")
            ?? throw new InvalidOperationException(
                "The embedded Desktop Shrine logo is unavailable.");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    private async void QueueSliderChange(
        TaskbarSliderDefinition definition,
        float value)
    {
        if (pendingChanges.Remove(definition.Id, out var previous))
            previous.Cancel();
        var cancellation = new CancellationTokenSource();
        pendingChanges[definition.Id] = cancellation;
        try
        {
            await Task.Delay(35, cancellation.Token);
            var callback = valueChanged;
            if (callback is not null)
            {
                await callback(
                    new(definition, value),
                    cancellation.Token);
            }
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (pendingChanges.TryGetValue(definition.Id, out var current)
                && ReferenceEquals(current, cancellation))
            {
                pendingChanges.Remove(definition.Id);
            }
            cancellation.Dispose();
        }
    }

    private async void ApplyChoiceChange(
        TaskbarChoiceDefinition definition,
        string value)
    {
        var callback = valueChanged;
        if (callback is null)
            return;
        await callback(
            new(definition, value),
            CancellationToken.None);
    }

    private async void ApplyToggleChange(
        TaskbarToggleDefinition definition,
        bool value)
    {
        var callback = valueChanged;
        if (callback is null)
            return;
        await callback(new(definition, value), CancellationToken.None);
    }

    private void RefreshUi(bool refreshChoices)
    {
        foreach (var id in sliders.Keys.Concat(choices.Keys).Concat(toggles.Keys).ToArray())
            RefreshControl(id, refreshChoices);
        if (notifyIcon is not null)
        {
            var brightness = ReadFloatValue("blinkstick.brightness", float.NaN);
            var text = float.IsFinite(brightness)
                ? $"Desktop Shrine - brightness {brightness:P0}"
                : $"Desktop Shrine {DesktopShrineProductInfo.VersionDisplay}";
            notifyIcon.Text = text.Length <= 63 ? text : text[..63];
        }
    }

    private void RefreshControl(string id, bool refreshChoice = false)
    {
        if (sliders.TryGetValue(id, out var slider))
            slider.SetValue(ReadFloatValue(id, slider.Value));
        if (choices.TryGetValue(id, out var choice))
        {
            choice.SetValue(ReadStringValue(id, choice.Value));
            if (refreshChoice)
                choice.RefreshChoices();
        }
        if (toggles.TryGetValue(id, out var toggle))
            toggle.SetValue(ReadBoolValue(id, toggle.Value));
    }

    private float ReadFloatValue(string id, float fallback)
    {
        lock (gate)
        {
            if (!currentValues.TryGetValue(id, out var value))
                return fallback;
            return value switch
            {
                float number => number,
                double number => (float)number,
                _ when float.TryParse(
                    Convert.ToString(
                        value,
                        System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed) => parsed,
                _ => fallback
            };
        }
    }

    private string ReadStringValue(string id, string fallback)
    {
        lock (gate)
            return currentValues.TryGetValue(id, out var value)
                ? Convert.ToString(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture)
                    ?? fallback
                : fallback;
    }

    private bool ReadBoolValue(string id, bool fallback)
    {
        lock (gate)
        {
            if (!currentValues.TryGetValue(id, out var value))
                return fallback;
            return value switch
            {
                bool flag => flag,
                _ when bool.TryParse(Convert.ToString(value), out var parsed) => parsed,
                _ => fallback
            };
        }
    }

    private void Post(Action action)
    {
        Control? currentDispatcher;
        lock (gate)
            currentDispatcher = dispatcher;
        if (currentDispatcher is null)
            return;
        try
        {
            currentDispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
