using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using DesktopShrine.Runtime;
using DesktopShrine.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

const string launchArgument = "--launch";
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
builder.Services.AddDesktopShrineRuntime(builder.Configuration);
using var host = builder.Build();
host.Services.GetRequiredService<LocalAppDataMigration>()
    .Migrate(DesktopShrinePaths.Current);
SeedPluginConfiguration(pluginConfigurationDirectory);
await host.RunAsync();

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
