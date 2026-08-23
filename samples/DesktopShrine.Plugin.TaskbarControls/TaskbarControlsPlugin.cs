using System.Globalization;
using DesktopShrine.Abstractions;
using DesktopShrine.Storage;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Plugin.TaskbarControls;

public sealed class TaskbarControlsPlugin : IOutputPlugin
{
    private readonly object valueGate = new();
    private readonly object audioDeviceGate = new();
    private readonly Func<ITaskbarIcon> iconFactory;
    private readonly IAudioDeviceProvider audioDeviceProvider;
    private readonly Dictionary<string, object> currentValues = [];
    private IPluginConfigurationEditor? configurationEditor;
    private TaskbarControlsSettings? settings;
    private IReadOnlyList<PluginSettingsGroup> groups = [];
    private IReadOnlyList<string> quickAccess = [];
    private TaskbarMenuDefinition? menu;
    private ITaskbarIcon? icon;
    private ILogger<TaskbarControlsPlugin>? logger;
    private IApplicationControl? applicationControl;
    private IReadOnlyList<TaskbarChoice> audioDevices = [new("default", "Default device")];
    private CancellationTokenSource? audioRefreshCancellation;
    private Task? audioRefreshTask;
    private bool started;
    private bool disposed;

    public TaskbarControlsPlugin() : this(() => new WindowsTaskbarIcon(), new WindowsAudioDeviceProvider()) { }

    internal TaskbarControlsPlugin(Func<ITaskbarIcon> iconFactory, IAudioDeviceProvider? audioDeviceProvider = null)
    {
        this.iconFactory = iconFactory ?? throw new ArgumentNullException(nameof(iconFactory));
        this.audioDeviceProvider = audioDeviceProvider ?? new WindowsAudioDeviceProvider();
    }

    public PluginDescriptor Descriptor { get; } = new()
    {
        Id = "taskbar-controls", Name = "Taskbar Controls", Version = new(2, 0, 0),
        Description = "Desktop Shrine settings and Windows notification-area controls.",
        SupportedPlatforms = ["windows"]
    };

    public IReadOnlyCollection<RequiredPortDescriptor> RequiredPorts => [];

    public ValueTask InitialiseAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger = context.LoggerFactory.CreateLogger<TaskbarControlsPlugin>();
        settings = TaskbarControlsSettings.FromConfiguration(context.Configuration);
        configurationEditor = context.ConfigurationEditor
            ?? throw new InvalidOperationException("The host does not provide plugin configuration editing.");
        applicationControl = context.ApplicationControl
            ?? throw new InvalidOperationException("The host does not provide application shutdown control.");
        groups = PluginSettingsCatalog.Discover(Path.Combine(AppContext.BaseDirectory, "plugins"));
        if (groups.Count == 0) groups = CreateCompatibilityCatalog(settings);
        quickAccess = PluginSettingsCatalog.ParseQuickAccess(settings.QuickAccess);
        RefreshAudioDevices();
        menu = BuildMenu();
        RefreshCurrentValues(menu.Controls);
        icon = iconFactory();
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started) return;
        IReadOnlyDictionary<string, object> values;
        lock (valueGate) values = new Dictionary<string, object>(currentValues);
        await icon!.StartAsync(menu!, values, ApplyChangeAsync, CreateSettingsDefinition(),
            applicationControl!.RequestShutdown, cancellationToken);
        audioRefreshCancellation = new CancellationTokenSource();
        audioRefreshTask = RefreshAudioDevicesAsync(audioRefreshCancellation.Token);
        started = true;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (!started) return;
        await StopAudioRefreshAsync();
        await icon!.StopAsync(cancellationToken);
        started = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (started) { await StopAudioRefreshAsync(); await icon!.StopAsync(CancellationToken.None); }
        if (icon is not null) await icon.DisposeAsync();
        started = false;
        icon = null;
    }

    private TaskbarMenuDefinition BuildMenu()
    {
        var indexed = groups.SelectMany(group => group.Settings)
            .ToDictionary(setting => setting.Id, StringComparer.OrdinalIgnoreCase);
        var controls = quickAccess.Take(5)
            .Select(id => indexed.TryGetValue(id, out var setting) ? ToTaskbarControl(setting) : null)
            .Where(control => control is not null).Cast<TaskbarControlDefinition>().ToArray();
        return new(controls,
        [
            new("settings", "Open Desktop Shrine / Settings", TaskbarCommandKind.OpenSettings),
            new("restart", "Restart Desktop Shrine", TaskbarCommandKind.Restart),
            new("exit", "Exit Desktop Shrine", TaskbarCommandKind.Exit)
        ]);
    }

    private TaskbarControlDefinition? ToTaskbarControl(PluginSettingDefinition setting)
    {
        if (!setting.CanAddToQuickAccess) return null;
        var name = setting.DisplayName.TrimEnd(' ', '*');
        return setting.ControlType switch
        {
            PluginSettingControlType.Slider => new TaskbarSliderDefinition(
                setting.Id, name, setting.PluginId, setting.SettingPath,
                (float)setting.Minimum!.Value, (float)setting.Maximum!.Value,
                (float)setting.Step!.Value,
                string.Equals(setting.Mapping, "logarithmic", StringComparison.OrdinalIgnoreCase)
                    ? TaskbarSliderMapping.Logarithmic : TaskbarSliderMapping.Linear,
                100d,
                string.Equals(setting.Format, "percent", StringComparison.OrdinalIgnoreCase)
                    ? TaskbarSliderValueFormat.Percent : TaskbarSliderValueFormat.DecimalOne),
            PluginSettingControlType.Toggle => new TaskbarToggleDefinition(
                setting.Id, name, setting.PluginId, setting.SettingPath),
            PluginSettingControlType.Choice => new TaskbarChoiceDefinition(
                setting.Id, name, setting.PluginId, setting.SettingPath, () => GetChoices(setting)),
            _ => null
        };
    }

    private DesktopShrineSettingsDefinition CreateSettingsDefinition()
    {
        var paths = DesktopShrinePaths.Current;
        return new(groups, ReadValue, GetChoices, ApplySettingAsync, () => quickAccess.ToArray(),
            SetQuickAccessAsync,
            new(AppContext.BaseDirectory, paths.Root, paths.ConfigurationDirectory,
                paths.LogsDirectory, Path.Combine(AppContext.BaseDirectory, "plugins")),
            applicationControl!.RequestShutdown);
    }

    private object ReadValue(PluginSettingDefinition setting) => PluginSettingsCatalog.ParseValue(
        setting, configurationEditor!.GetValue(setting.PluginId, setting.SettingPath));

    private async ValueTask ApplySettingAsync(PluginSettingDefinition setting, object value,
        CancellationToken cancellationToken)
    {
        await configurationEditor!.SetValueAsync(setting.PluginId, setting.SettingPath, value, cancellationToken);
        lock (valueGate) currentValues[setting.Id] = value;
        icon?.UpdateValue(setting.Id, value);
        logger!.LogInformation("Set {PluginId} {SettingPath} to {Value}", setting.PluginId, setting.SettingPath, value);
    }

    private async ValueTask SetQuickAccessAsync(IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        quickAccess = values.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        await configurationEditor!.SetValueAsync(Descriptor.Id, "QuickAccess",
            PluginSettingsCatalog.SerialiseQuickAccess(quickAccess), cancellationToken);
        menu = BuildMenu();
        RefreshCurrentValues(menu.Controls);
        IReadOnlyDictionary<string, object> snapshot;
        lock (valueGate) snapshot = new Dictionary<string, object>(currentValues);
        icon?.UpdateMenu(menu, snapshot);
    }

    private async ValueTask ApplyChangeAsync(TaskbarControlChange change,
        CancellationToken cancellationToken)
    {
        var setting = groups.SelectMany(group => group.Settings).FirstOrDefault(item =>
            string.Equals(item.Id, change.Control.Id, StringComparison.OrdinalIgnoreCase));
        if (setting is null) return;
        try { await ApplySettingAsync(setting, change.Value, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger!.LogError(exception, "Could not update {PluginId} {SettingPath}",
                change.Control.TargetPluginId, change.Control.SettingPath);
        }
    }

    private void RefreshCurrentValues(IEnumerable<TaskbarControlDefinition> controls)
    {
        lock (valueGate)
        {
            foreach (var control in controls)
            {
                var setting = groups.SelectMany(group => group.Settings).First(item =>
                    string.Equals(item.Id, control.Id, StringComparison.OrdinalIgnoreCase));
                currentValues[control.Id] = ReadValue(setting);
            }
        }
    }

    private IReadOnlyList<TaskbarChoice> GetChoices(PluginSettingDefinition setting)
    {
        if (string.Equals(setting.ChoiceProvider, "audio-devices", StringComparison.OrdinalIgnoreCase))
        { lock (audioDeviceGate) return audioDevices; }
        return setting.Choices.Select(choice => new TaskbarChoice(choice.Value, choice.DisplayName)).ToArray();
    }

    private void RefreshAudioDevices()
    {
        try
        {
            var captureMode = configurationEditor!.GetValue("audio-collector", "CaptureMode") ?? "loopback";
            var refreshed = audioDeviceProvider.GetDevices(captureMode);
            if (refreshed.Count > 0) lock (audioDeviceGate) audioDevices = refreshed.ToArray();
        }
        catch (Exception exception) { logger!.LogWarning(exception, "Could not enumerate audio devices"); }
    }

    private async Task RefreshAudioDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                RefreshAudioDevices();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async ValueTask StopAudioRefreshAsync()
    {
        var cancellation = audioRefreshCancellation;
        var task = audioRefreshTask;
        audioRefreshCancellation = null; audioRefreshTask = null;
        if (cancellation is null) return;
        await cancellation.CancelAsync();
        if (task is not null) await task;
        cancellation.Dispose();
    }

    private static IReadOnlyList<PluginSettingsGroup> CreateCompatibilityCatalog(TaskbarControlsSettings value) =>
    [new("blinkstick-bar", "BlinkStick", null,
    [
        new PluginSettingDefinition { Id = "blinkstick.brightness", PluginId = "blinkstick-bar", PluginDisplayName = "BlinkStick", SettingPath = "Brightness", DisplayName = value.Brightness.DisplayName, ControlType = PluginSettingControlType.Slider, DefaultValue = value.Brightness.DefaultValue.ToString(CultureInfo.InvariantCulture), Minimum = value.Brightness.Minimum, Maximum = value.Brightness.Maximum, Step = value.Brightness.Step, Mapping = "logarithmic", Format = "percent", CanAddToQuickAccess = true },
        new PluginSettingDefinition { Id = "blinkstick.gamma", PluginId = "blinkstick-bar", PluginDisplayName = "BlinkStick", SettingPath = "Gamma", DisplayName = value.Gamma.DisplayName, ControlType = PluginSettingControlType.Slider, DefaultValue = value.Gamma.DefaultValue.ToString(CultureInfo.InvariantCulture), Minimum = value.Gamma.Minimum, Maximum = value.Gamma.Maximum, Step = value.Gamma.Step, Format = "decimal-one", CanAddToQuickAccess = true }
    ]),
    new("audio-collector", "Audio", null,
    [
        new PluginSettingDefinition { Id = "audio.device", PluginId = "audio-collector", PluginDisplayName = "Audio", SettingPath = "Device", DisplayName = value.AudioDevice.DisplayName, ControlType = PluginSettingControlType.Choice, DefaultValue = "default", ChoiceProvider = "audio-devices", CanAddToQuickAccess = true }
    ])];
}
