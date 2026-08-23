using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using DesktopShrine.Abstractions;
using DesktopShrine.Runtime;
using DesktopShrine.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

const string launchArgument = "--launch";
if (RestartProcessHandoff.TryParse(
        args,
        out var previousProcessId,
        out var forwardedArguments))
{
    await RestartProcessHandoff.WaitForExitAsync(previousProcessId);
    StartProcess(forwardedArguments);
    return;
}

var launchRequested = args.Any(argument =>
    string.Equals(argument, launchArgument, StringComparison.OrdinalIgnoreCase));
if (launchRequested
    && OperatingSystem.IsWindows()
    && ShouldRunAsAdministrator()
    && !IsAdministrator())
{
    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The Desktop Shrine executable path is unavailable."),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in args.Where(argument =>
                     !string.Equals(
                         argument,
                         launchArgument,
                         StringComparison.OrdinalIgnoreCase)))
            startInfo.ArgumentList.Add(argument);

        Process.Start(startInfo);
    }
    catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
    {
        // The user cancelled the UAC prompt.
    }

    return;
}

using var instanceMutex = new Mutex(
    initiallyOwned: true,
    "Local\\DesktopShrine.Host",
    out var isFirstInstance);
if (!isFirstInstance)
    return;

var dataDirectory = Environment.GetEnvironmentVariable(
    "DESKTOP_SHRINE_DATA_DIRECTORY");
if (string.IsNullOrWhiteSpace(dataDirectory))
    dataDirectory = DesktopShrinePaths.Current.Root;

var pluginConfigurationDirectory = Path.Combine(
    dataDirectory,
    "configuration",
    "plugins");
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["DesktopShrine:PluginConfigurationDirectory"] =
        pluginConfigurationDirectory,
    ["DesktopShrine:OutputProfileFile"] = Path.Combine(
        dataDirectory,
        "configuration",
        "output-input-profiles.json")
});
builder.Logging.ClearProviders();
builder.Logging.AddDebug();
if (string.Equals(
        Environment.GetEnvironmentVariable(
            "DESKTOP_SHRINE_CONSOLE_LOGGING"),
        "1",
        StringComparison.Ordinal))
{
    builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
}
builder.Services.AddDesktopShrineRuntime(builder.Configuration);
var restartRequested = false;
using (var host = builder.Build())
{
    host.Services.GetRequiredService<LocalAppDataMigration>()
        .Migrate(DesktopShrinePaths.Current);
    SeedPluginConfiguration(pluginConfigurationDirectory);
    var shutdown = host.Services
        .GetRequiredService<ApplicationShutdownCoordinator>();
    SessionEndingEventHandler? sessionEnding = null;
    if (OperatingSystem.IsWindows())
    {
        sessionEnding = (_, eventArgs) =>
        {
            host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("WindowsSession")
                .LogInformation(
                    "Windows session ending: {Reason}",
                    eventArgs.Reason);
            shutdown.RequestShutdown(ApplicationShutdownKind.Exit);
        };
        SystemEvents.SessionEnding += sessionEnding;
    }
    try
    {
        await host.RunAsync();
    }
    finally
    {
        if (sessionEnding is not null)
            SystemEvents.SessionEnding -= sessionEnding;
    }
    restartRequested = shutdown.RestartRequested;
}

if (restartRequested)
{
    StartRestartHandoff(args);
}

static bool ShouldRunAsAdministrator()
{
    using var key = Registry.CurrentUser.OpenSubKey(
        @"Software\Desktop Shrine");
    return key?.GetValue("RunAsAdministrator") switch
    {
        int value => value != 0,
        string value when int.TryParse(value, out var parsed) => parsed != 0,
        _ => true
    };
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(
        WindowsBuiltInRole.Administrator);
}

static void StartRestartHandoff(IReadOnlyList<string> originalArguments)
{
    StartProcess(RestartProcessHandoff.CreateArguments(
        Environment.ProcessId,
        originalArguments));
}

static void StartProcess(IReadOnlyList<string> arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The Desktop Shrine executable path is unavailable."),
        UseShellExecute = true,
        WorkingDirectory = AppContext.BaseDirectory
    };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);
    Process.Start(startInfo);
}

static void SeedPluginConfiguration(string destinationDirectory)
{
    Directory.CreateDirectory(destinationDirectory);
    var sourceDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "configuration",
        "plugins");
    if (!Directory.Exists(sourceDirectory))
        return;

    foreach (var sourcePath in Directory.EnumerateFiles(
                 sourceDirectory,
                 "*.json",
                 SearchOption.TopDirectoryOnly))
    {
        var destinationPath = Path.Combine(
            destinationDirectory,
            Path.GetFileName(sourcePath));
        if (!File.Exists(destinationPath))
            File.Copy(sourcePath, destinationPath);
    }
}
