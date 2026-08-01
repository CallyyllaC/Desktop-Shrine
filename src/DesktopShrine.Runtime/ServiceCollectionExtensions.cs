using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopShrine.Runtime;
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDesktopShrineRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DesktopShrineOptions>(configuration.GetSection("DesktopShrine"));
        services.PostConfigure<DesktopShrineOptions>(options =>
        {
            options.PluginDirectory = FromApplicationDirectory(options.PluginDirectory);
            options.ContractDirectory = FromApplicationDirectory(options.ContractDirectory);
            options.PluginConfigurationDirectory =
                FromApplicationDirectory(options.PluginConfigurationDirectory);
            options.OutputProfileFile =
                FromApplicationDirectory(options.OutputProfileFile);
        });
        services.AddSingleton<IContractRegistry, ContractRegistry>();
        services.AddSingleton<IInputActivityRegistry, InputActivityRegistry>();
        services.AddSingleton<IPortRegistry, PortRegistry>();
        services.AddSingleton<IRouteTable, RouteTable>();
        services.AddSingleton<IPortBus, PortBus>();
        services.AddSingleton<IInputResolver, InputResolver>();
        services.AddSingleton<IOutputInputProfileStore, JsonOutputInputProfileStore>();
        services.AddSingleton<
            IOutputInputProfileService,
            OutputInputProfileService>();
        services.AddSingleton<
            IInputRoutingCoordinator,
            InputRoutingCoordinator>();
        services.AddSingleton<IContractPackageCatalog, ContractPackageCatalog>(); services.AddSingleton<ContractPackageLoader>(); services.AddSingleton<IPluginCatalog, PluginCatalog>(); services.AddSingleton<IPluginLoader, PluginAssemblyLoader>();
        services.AddSingleton<IPluginConfigurationProvider, PluginConfigurationProvider>(); services.AddSingleton<IPluginLifecycleManager, PluginLifecycleManager>(); services.AddSingleton<IRuntimeDiagnostics, RuntimeDiagnostics>();
        services.AddHostedService<ShrineRuntimeHostedService>(); return services;
    }

    private static string FromApplicationDirectory(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}
