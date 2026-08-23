using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopShrine.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DesktopShrine.Runtime;
public sealed class DesktopShrineOptions
{
    public string PluginDirectory { get; set; } = "plugins";
    public string ContractDirectory { get; set; } = "contracts";
    public string PluginConfigurationDirectory { get; set; } =
        "configuration/plugins";
    public string OutputProfileFile { get; set; } =
        "configuration/output-input-profiles.json";
}
public sealed record ContractPackageDependency(string PackageId, string VersionRange);
public sealed record PluginManifest { public required string Id { get; init; } public required string Version { get; init; } public required string EntryAssembly { get; init; } public required string EntryType { get; init; } public string? MinimumHostVersion { get; init; } public IReadOnlyCollection<string> SupportedPlatforms { get; init; } = []; public IReadOnlyCollection<ContractPackageDependency> ContractDependencies { get; init; } = []; }
public sealed record DiscoveredPlugin(string DirectoryPath, string ManifestPath, PluginManifest Manifest);
public interface IPluginCatalog { ValueTask<IReadOnlyCollection<DiscoveredPlugin>> DiscoverAsync(CancellationToken cancellationToken); }
public sealed class PluginCatalog(IOptions<DesktopShrineOptions> options, ILogger<PluginCatalog> logger) : IPluginCatalog { private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true }; public async ValueTask<IReadOnlyCollection<DiscoveredPlugin>> DiscoverAsync(CancellationToken token) { var result = new List<DiscoveredPlugin>(); if (!Directory.Exists(options.Value.PluginDirectory)) return result; foreach (var path in Directory.EnumerateFiles(options.Value.PluginDirectory, "plugin.json", SearchOption.AllDirectories)) try { await using var s = File.OpenRead(path); var manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(s, JsonOptions, token) ?? throw new InvalidDataException("Empty manifest"); result.Add(new(Path.GetDirectoryName(path)!, path, manifest)); } catch (Exception ex) { logger.LogError(ex, "Cannot discover {Manifest}", path); } return result; } }
public sealed class PluginLoadContext : AssemblyLoadContext { private readonly AssemblyDependencyResolver resolver; private readonly HashSet<string> shared; public PluginLoadContext(string path, IEnumerable<string> sharedAssemblies) : base(isCollectible: false) { resolver = new(path); shared = new(sharedAssemblies, StringComparer.OrdinalIgnoreCase); } protected override Assembly? Load(AssemblyName name) { if (name.Name is not null && (shared.Contains(name.Name) || name.Name.StartsWith("Microsoft.Extensions."))) return null; var path = resolver.ResolveAssemblyToPath(name); return path is null ? null : LoadFromAssemblyPath(path); } protected override nint LoadUnmanagedDll(string name) { var path = resolver.ResolveUnmanagedDllToPath(name); return path is null ? 0 : LoadUnmanagedDllFromPath(path); } }
public sealed class LoadedPlugin { public required IShrinePlugin Instance { get; init; } public required PluginManifest Manifest { get; init; } public required PluginLoadContext LoadContext { get; init; } public PluginLifecycleState State { get; set; } = PluginLifecycleState.Loaded; public Exception? LastError { get; set; } public DateTimeOffset? StartedAt { get; set; } public DateTimeOffset? StoppedAt { get; set; } }
public interface IPluginLoader { ValueTask<LoadedPlugin> LoadAsync(DiscoveredPlugin plugin, CancellationToken cancellationToken); }
public sealed class PluginAssemblyLoader(IContractRegistry contracts) : IPluginLoader
{
    private static readonly string[] SharedPlatformAssemblies = ["WinRT.Runtime", "Microsoft.Windows.SDK.NET"];

    public ValueTask<LoadedPlugin> LoadAsync(DiscoveredPlugin p, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(Path.Combine(p.DirectoryPath, p.Manifest.EntryAssembly));
        foreach (var assemblyName in SharedPlatformAssemblies)
            LoadSharedAssemblyIfPresent(p.DirectoryPath, assemblyName);

        var shared = new[] { typeof(IShrinePlugin).Assembly.GetName().Name! }
            .Concat(contracts.Contracts.Select(x => x.PayloadType.Assembly.GetName().Name!))
            .Concat(SharedPlatformAssemblies);
        var context = new PluginLoadContext(path, shared);
        var assembly = context.LoadFromAssemblyPath(path);
        var type = assembly.GetType(p.Manifest.EntryType, true)!;
        var instance = Activator.CreateInstance(type) as IShrinePlugin ?? throw new InvalidOperationException("Entry type is not an IShrinePlugin.");
        if (instance.Descriptor.Id != p.Manifest.Id)
            throw new InvalidOperationException("Manifest and descriptor IDs differ.");
        return ValueTask.FromResult(new LoadedPlugin { Instance = instance, Manifest = p.Manifest, LoadContext = context });
    }

    private static void LoadSharedAssemblyIfPresent(string pluginDirectory, string assemblyName)
    {
        if (AssemblyLoadContext.Default.Assemblies.Any(assembly => assembly.GetName().Name == assemblyName))
            return;

        var path = Path.GetFullPath(Path.Combine(pluginDirectory, $"{assemblyName}.dll"));
        if (File.Exists(path))
            AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
}
public interface IContractPackageCatalog { ValueTask<IReadOnlyCollection<string>> DiscoverAssemblyPathsAsync(CancellationToken cancellationToken); }
public sealed class ContractPackageCatalog(IOptions<DesktopShrineOptions> options) : IContractPackageCatalog { public ValueTask<IReadOnlyCollection<string>> DiscoverAssemblyPathsAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); IReadOnlyCollection<string> paths = Directory.Exists(options.Value.ContractDirectory) ? Directory.EnumerateFiles(options.Value.ContractDirectory, "*.dll").ToArray() : []; return ValueTask.FromResult(paths); } }
public sealed class ContractPackageLoader(IContractPackageCatalog catalog, IContractRegistry registry) { public async ValueTask LoadAsync(CancellationToken token) { foreach (var path in await catalog.DiscoverAssemblyPathsAsync(token)) registry.RegisterAssembly(AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path))); } }
public interface IPluginConfigurationProvider
{
    IConfiguration GetConfiguration(string pluginId);
}

public sealed class PluginConfigurationProvider(
    IConfiguration configuration,
    IOptions<DesktopShrineOptions> options,
    ILogger<PluginConfigurationProvider> logger) :
    IPluginConfigurationProvider,
    IPluginConfigurationEditor
{
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public IConfiguration GetConfiguration(string id)
    {
        var fileName = GetFileName(id);
        EnsurePlaceholderExists(fileName, id);

        // The plugin-owned JSON file provides its defaults. The conventional
        // Plugins:<id> section is layered over it so environment variables,
        // command-line arguments, and other host providers can still override
        // individual values.
        return new ConfigurationBuilder()
            .AddJsonFile(fileName, optional: true, reloadOnChange: true)
            .AddConfiguration(configuration.GetSection($"Plugins:{id}"))
            .Build();
    }

    public string? GetValue(string pluginId, string settingPath)
    {
        ValidateSettingPath(settingPath);
        var pluginConfiguration = GetConfiguration(pluginId);
        try
        {
            return pluginConfiguration[settingPath];
        }
        finally
        {
            (pluginConfiguration as IDisposable)?.Dispose();
        }
    }

    public async ValueTask SetValueAsync(
        string pluginId,
        string settingPath,
        object? value,
        CancellationToken cancellationToken = default)
    {
        ValidateSettingPath(settingPath);
        var fileName = GetFileName(pluginId);
        await writeGate.WaitAsync(cancellationToken);
        string? temporaryFileName = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fileName)!);
            JsonObject root;
            if (File.Exists(fileName))
            {
                await using var source = new FileStream(
                    fileName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                root = await JsonNode.ParseAsync(
                           source,
                           cancellationToken: cancellationToken)
                       as JsonObject
                    ?? throw new InvalidOperationException(
                        $"Plugin configuration {fileName} must contain a JSON object.");
            }
            else
            {
                root = [];
            }

            SetNode(root, settingPath, JsonSerializer.SerializeToNode(value));
            temporaryFileName = Path.Combine(
                Path.GetDirectoryName(fileName)!,
                $".{Path.GetFileName(fileName)}.{Guid.NewGuid():N}.tmp");
            await using (var destination = new FileStream(
                             temporaryFileName,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    destination,
                    root,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryFileName, fileName, overwrite: true);
            temporaryFileName = null;
            logger.LogInformation(
                "Updated setting {SettingPath} for plugin {PluginId}",
                settingPath,
                pluginId);
        }
        finally
        {
            if (temporaryFileName is not null)
            {
                try
                {
                    File.Delete(temporaryFileName);
                }
                catch (IOException)
                {
                }
            }
            writeGate.Release();
        }
    }

    private string GetFileName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || id.Contains(Path.DirectorySeparatorChar)
            || id.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException(
                "Plugin ID cannot be used as a configuration file name.",
                nameof(id));

        return Path.Combine(
            options.Value.PluginConfigurationDirectory,
            $"{id}.json");
    }

    private static void ValidateSettingPath(string settingPath)
    {
        if (string.IsNullOrWhiteSpace(settingPath)
            || settingPath.Split(':').Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                "Setting path must contain non-empty colon-separated segments.",
                nameof(settingPath));
    }

    private static void SetNode(
        JsonObject root,
        string settingPath,
        JsonNode? value)
    {
        var segments = settingPath.Split(':');
        var current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            if (current[segment] is not JsonObject child)
            {
                child = [];
                current[segment] = child;
            }
            current = child;
        }
        current[segments[^1]] = value;
    }

    private void EnsurePlaceholderExists(string fileName, string pluginId)
    {
        if (File.Exists(fileName))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fileName)!);
            using var stream = new FileStream(
                fileName,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine("{}");
            logger.LogInformation(
                "Created placeholder configuration for plugin {PluginId} at {ConfigurationFile}",
                pluginId,
                fileName);
        }
        catch (IOException) when (File.Exists(fileName))
        {
            // Another startup path created the same placeholder first.
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Could not create placeholder configuration for plugin {PluginId} at {ConfigurationFile}; using plugin defaults",
                pluginId,
                fileName);
        }
    }
}
public sealed class PluginContext(
    string id,
    IConfiguration configuration,
    ILoggerFactory logs,
    IPluginPublisher publisher,
    IPluginSubscriber subscriber,
    ILiveConfiguration<OutputInputProfile>? inputProfile,
    IPluginConfigurationEditor? configurationEditor = null,
    IApplicationControl? applicationControl = null) :
    IPluginContext,
    IDisposable
{
    private readonly List<IDisposable> monitors = [];
    private bool disposed;

    public string PluginId => id;
    public IConfiguration Configuration => configuration;
    public ILoggerFactory LoggerFactory => logs;
    public IPluginPublisher Publisher => publisher;
    public IPluginSubscriber Subscriber => subscriber;
    public ILiveConfiguration<OutputInputProfile>? InputProfile => inputProfile;
    public IPluginConfigurationEditor? ConfigurationEditor =>
        configurationEditor;
    public IApplicationControl? ApplicationControl => applicationControl;

    public ILiveConfiguration<TConfig> ObserveConfiguration<TConfig>(
        Func<IConfiguration, TConfig> snapshotFactory,
        Func<TConfig, ConfigurationValidationResult>? validator = null)
        where TConfig : notnull
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var monitor = new ConfigurationSectionMonitor<TConfig>(
            configuration,
            snapshotFactory,
            validator,
            logs.CreateLogger($"PluginConfiguration.{id}"));
        lock (monitors)
            monitors.Add(monitor);
        return monitor;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lock (monitors)
        {
            foreach (var monitor in monitors)
                monitor.Dispose();
            monitors.Clear();
        }
        if (configuration is IDisposable disposableConfiguration)
            disposableConfiguration.Dispose();
    }
}
public interface IPluginLifecycleManager { IReadOnlyCollection<LoadedPlugin> Plugins { get; } ValueTask InitialiseAllAsync(CancellationToken cancellationToken); ValueTask StartAllAsync(CancellationToken cancellationToken); ValueTask StopAllAsync(CancellationToken cancellationToken); }
internal sealed class PluginLifecycleManager(
    IPortRegistry ports,
    IContractRegistry contracts,
    IPortBus bus,
    IRouteTable routes,
    IPluginConfigurationProvider configs,
    IOutputInputProfileService profiles,
    IApplicationControl applicationControl,
    OutputShutdownCoordinator outputShutdown,
    ILoggerFactory logs,
    ILogger<PluginLifecycleManager> logger) : IPluginLifecycleManager
{
    private readonly List<LoadedPlugin> plugins = [];
    private readonly List<LoadedPlugin> started = [];
    private readonly Dictionary<string, PluginContext> contexts = [];
    public IReadOnlyCollection<LoadedPlugin> Plugins => plugins.ToArray();
    public void Add(LoadedPlugin p)
    {
        plugins.Add(p);
        if (p.Instance is IShutdownOutputParticipant shutdownOutput)
            outputShutdown.Register(shutdownOutput);
    }

    public async ValueTask InitialiseAllAsync(CancellationToken token)
    {
        foreach (var p in plugins.Where(x => x.State == PluginLifecycleState.Loaded))
            try
            {
                var id = p.Instance.Descriptor.Id;
                var ctx = new PluginContext(
                    id,
                    configs.GetConfiguration(id),
                    logs,
                    new PluginPublisher(id, ports, contracts, bus),
                    new PluginSubscriber(id, ports, contracts, bus, routes),
                    p.Instance is IOutputPlugin { RequiredPorts.Count: > 0 }
                        ? profiles.GetLiveProfile(id)
                        : null,
                    configs as IPluginConfigurationEditor,
                    applicationControl);
                contexts.Add(id, ctx);
                await p.Instance.InitialiseAsync(ctx, token);
                p.State = PluginLifecycleState.Initialised;
            }
            catch (Exception ex)
            {
                p.State = PluginLifecycleState.Faulted;
                p.LastError = ex;
                if (contexts.Remove(p.Instance.Descriptor.Id, out var context))
                    context.Dispose();
                logger.LogError(
                    ex,
                    "Plugin {PluginId} could not initialise",
                    p.Instance.Descriptor.Id);
            }
    }
    public async ValueTask StartAllAsync(CancellationToken token) { var ordered = plugins.OrderByDescending(x => x.Instance is IOutputPlugin); foreach (var p in ordered.Where(x => x.State == PluginLifecycleState.Initialised)) try { p.State = PluginLifecycleState.Starting; await p.Instance.StartAsync(token); p.State = PluginLifecycleState.Running; p.StartedAt = DateTimeOffset.UtcNow; started.Add(p); } catch (Exception ex) { p.State = PluginLifecycleState.Faulted; p.LastError = ex; logger.LogError(ex, "Plugin {PluginId} could not start", p.Instance.Descriptor.Id); } }
    public async ValueTask StopAllAsync(CancellationToken token)
    {
        foreach (var plugin in started.AsEnumerable().Reverse())
        {
            try
            {
                plugin.State = PluginLifecycleState.Stopping;
                await plugin.Instance.StopAsync(token);
                plugin.State = PluginLifecycleState.Stopped;
                plugin.StoppedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception exception)
            {
                plugin.State = PluginLifecycleState.Faulted;
                plugin.LastError = exception;
                logger.LogError(
                    exception,
                    "Plugin {PluginId} could not stop",
                    plugin.Instance.Descriptor.Id);
            }
            await DisposePluginAsync(plugin);
        }
        started.Clear();

        // A plugin can acquire resources during InitialiseAsync or partially
        // through StartAsync before startup fails. Dispose any such remaining
        // contexts through the same deterministic shutdown path.
        foreach (var plugin in plugins.AsEnumerable().Reverse().Where(plugin =>
                     contexts.ContainsKey(plugin.Instance.Descriptor.Id)))
        {
            await DisposePluginAsync(plugin);
        }
    }

    private async ValueTask DisposePluginAsync(LoadedPlugin plugin)
    {
        try
        {
            await plugin.Instance.DisposeAsync();
        }
        catch (Exception exception)
        {
            plugin.State = PluginLifecycleState.Faulted;
            plugin.LastError = exception;
            logger.LogError(
                exception,
                "Plugin {PluginId} could not dispose cleanly",
                plugin.Instance.Descriptor.Id);
        }
        finally
        {
            if (contexts.Remove(plugin.Instance.Descriptor.Id, out var context))
                context.Dispose();
        }
    }
}
public sealed record PluginSnapshot(string Id, PluginLifecycleState State, string? Error);
public sealed record BindingSnapshot(PortAddress Provider, PortAddress Consumer);
public sealed record ContractSnapshot(string Id, Version Version, string PayloadType);
public sealed record RuntimeSnapshot { public required IReadOnlyCollection<PluginSnapshot> Plugins { get; init; } public required IReadOnlyCollection<BindingSnapshot> Bindings { get; init; } public required IReadOnlyCollection<ContractSnapshot> Contracts { get; init; } }
public interface IRuntimeDiagnostics { RuntimeSnapshot GetSnapshot(); }
public sealed class RuntimeDiagnostics(IPluginLifecycleManager lifecycle, IRouteTable routes, IContractRegistry contracts) : IRuntimeDiagnostics { public RuntimeSnapshot GetSnapshot() => new() { Plugins = lifecycle.Plugins.Select(x => new PluginSnapshot(x.Instance.Descriptor.Id, x.State, x.LastError?.Message)).ToArray(), Bindings = routes.Bindings.Select(x => new BindingSnapshot(x.Provider, x.Consumer)).ToArray(), Contracts = contracts.Contracts.Select(x => new ContractSnapshot(x.Reference.ContractId, x.Reference.Version, x.PayloadType.FullName!)).ToArray() }; }
public sealed class ShrineRuntimeHostedService(
    ContractPackageLoader contractLoader,
    IPluginCatalog catalog,
    IPluginLoader loader,
    IPortRegistry ports,
    IOutputInputProfileService profiles,
    IInputRoutingCoordinator routing,
    IRouteTable routes,
    IPluginLifecycleManager lifecycle,
    ILogger<ShrineRuntimeHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken token)
    {
        await contractLoader.LoadAsync(token);
        var loaded = lifecycle as PluginLifecycleManager
                     ?? throw new InvalidOperationException();
        var ids = new HashSet<string>();
        foreach (var found in await catalog.DiscoverAsync(token))
            try
            {
                if (!ids.Add(found.Manifest.Id))
                    throw new InvalidOperationException(
                        $"Duplicate plugin ID {found.Manifest.Id}");
                var plugin = await loader.LoadAsync(found, token);
                ports.RegisterPlugin(plugin.Instance);
                loaded.Add(plugin);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Plugin {Plugin} could not load",
                    found.Manifest.Id);
            }

        await profiles.SynchroniseAsync(token);
        routing.Start();
        await lifecycle.InitialiseAllAsync(token);
        await lifecycle.StartAllAsync(token);
        logger.LogInformation(
            "Desktop Shrine running with {Plugins} plugins and {Bindings} bindings",
            lifecycle.Plugins.Count,
            routes.Bindings.Count);
    }

    public async Task StopAsync(CancellationToken token)
    {
        routing.Dispose();
        await lifecycle.StopAllAsync(token);
    }
}
