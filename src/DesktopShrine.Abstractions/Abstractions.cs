using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Abstractions;

public interface IShrineMessage;
public interface IShrineEvent : IShrineMessage;
public interface IShrineState : IShrineMessage;
public interface IShrineStreamFrame : IShrineMessage;
public enum DeliveryKind { Event, State, Stream }
public enum BindingCardinality { Single, Multiple }
public enum PluginLifecycleState { Discovered, Loaded, Initialised, Starting, Running, Stopping, Stopped, Faulted, Disabled }
public enum InputActivityState { Active, Inactive }
public enum DefaultInputPriorityPlacement { Lowest, Highest }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class ShrineContractAttribute(string contractId, string version, DeliveryKind deliveryKind) : Attribute
{ public string ContractId { get; } = contractId; public Version Version { get; } = Version.Parse(version); public DeliveryKind DeliveryKind { get; } = deliveryKind; }
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ShrineContractPackageAttribute(string packageId, string version) : Attribute
{ public string PackageId { get; } = packageId; public Version Version { get; } = Version.Parse(version); }

public sealed record VersionRange
{
    public Version? MinimumInclusive { get; init; }
    public Version? MaximumExclusive { get; init; }
    public static VersionRange Exact(Version version) => new() { MinimumInclusive = version, MaximumExclusive = Next(version) };
    public static VersionRange Between(Version minimumInclusive, Version maximumExclusive) => new() { MinimumInclusive = minimumInclusive, MaximumExclusive = maximumExclusive };
    public bool Contains(Version version) => (MinimumInclusive is null || version >= MinimumInclusive) && (MaximumExclusive is null || version < MaximumExclusive);
    private static Version Next(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build) + 1);
}
public sealed record ContractReference(string ContractId, Version Version);
public sealed record ContractRequirement(string ContractId, VersionRange AcceptedVersions);
public sealed record ContractDescriptor { public required ContractReference Reference { get; init; } public required Type PayloadType { get; init; } public required DeliveryKind DeliveryKind { get; init; } public required string PackageId { get; init; } }
public sealed record PluginDescriptor { public required string Id { get; init; } public required string Name { get; init; } public required Version Version { get; init; } public string? Description { get; init; } public IReadOnlyCollection<string> SupportedPlatforms { get; init; } = []; }
public sealed record ProvidedPortDescriptor { public required string PortId { get; init; } public required string DisplayName { get; init; } public required ContractReference Contract { get; init; } }
public sealed record RequiredPortDescriptor { public required string PortId { get; init; } public required string DisplayName { get; init; } public required ContractRequirement Requirement { get; init; } public bool IsRequired { get; init; } = true; public BindingCardinality Cardinality { get; init; } = BindingCardinality.Single; }
public sealed record PortAddress(string PluginId, string PortId);
public sealed record PortBinding { public required PortAddress Provider { get; init; } public required PortAddress Consumer { get; init; } public bool IsEnabled { get; init; } = true; }
public sealed record MessageEnvelope<T> where T : IShrineMessage { public required Guid MessageId { get; init; } public required DateTimeOffset Timestamp { get; init; } public required PortAddress Source { get; init; } public required ContractReference Contract { get; init; } public required T Payload { get; init; } public Guid? CorrelationId { get; init; } public long SequenceNumber { get; init; } }

public sealed class InputActivityStateChangedEventArgs(
    InputActivityState previous,
    InputActivityState current) : EventArgs
{
    public InputActivityState Previous { get; } = previous;
    public InputActivityState Current { get; } = current;
}

public sealed record InputPreference
{
    public required string InputId { get; init; }
    public required bool Enabled { get; init; }
    public required int Priority { get; init; }
}

public sealed record OutputInputProfile
{
    public required string OutputId { get; init; }
    public required IReadOnlyList<InputPreference> Inputs { get; init; }
}

public sealed class ConfigurationChangedEventArgs<TConfig>(
    TConfig previous,
    TConfig current) : EventArgs
{
    public TConfig Previous { get; } = previous;
    public TConfig Current { get; } = current;
}

public sealed record ConfigurationValidationResult
{
    public required bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public static ConfigurationValidationResult Success { get; } = new() { IsValid = true };

    public static ConfigurationValidationResult Failure(params string[] errors) =>
        new() { IsValid = false, Errors = errors };
}

public interface ILiveConfiguration<TConfig>
{
    TConfig Current { get; }
    event EventHandler<ConfigurationChangedEventArgs<TConfig>>? Changed;
}

public interface IPluginConfigurationEditor
{
    string? GetValue(string pluginId, string settingPath);

    ValueTask SetValueAsync(
        string pluginId,
        string settingPath,
        object? value,
        CancellationToken cancellationToken = default);
}

public sealed record InputRouteSnapshot
{
    public required PortAddress Consumer { get; init; }
    public required bool IsExclusive { get; init; }
    public required IReadOnlyList<PortAddress> Providers { get; init; }
    public PortAddress? SelectedProvider => IsExclusive ? Providers.FirstOrDefault() : null;
}

public interface IInputRouteMonitor
{
    InputRouteSnapshot Current { get; }
    event EventHandler<ConfigurationChangedEventArgs<InputRouteSnapshot>>? Changed;
}

public interface IShrinePlugin : IAsyncDisposable { PluginDescriptor Descriptor { get; } ValueTask InitialiseAsync(IPluginContext context, CancellationToken cancellationToken); ValueTask StartAsync(CancellationToken cancellationToken); ValueTask StopAsync(CancellationToken cancellationToken); }
public interface IInputPlugin : IShrinePlugin
{
    IReadOnlyCollection<ProvidedPortDescriptor> ProvidedPorts { get; }
    DefaultInputPriorityPlacement DefaultPriorityPlacement =>
        DefaultInputPriorityPlacement.Lowest;

    // Legacy inputs are active until they opt into explicit activity reporting.
    InputActivityState ActivityState => InputActivityState.Active;
    event EventHandler<InputActivityStateChangedEventArgs>? ActivityStateChanged
    {
        add { }
        remove { }
    }
}
public interface IOutputPlugin : IShrinePlugin { IReadOnlyCollection<RequiredPortDescriptor> RequiredPorts { get; } }
public interface IShutdownOutputParticipant
{
    void MuteOutputForShutdown();
    void BlackoutForShutdown();
}
public enum ApplicationShutdownKind
{
    Exit,
    Restart
}
public interface IApplicationControl
{
    void RequestShutdown(ApplicationShutdownKind kind);
}
public interface IPluginContext
{
    string PluginId { get; }
    IConfiguration Configuration { get; }
    ILoggerFactory LoggerFactory { get; }
    IPluginPublisher Publisher { get; }
    IPluginSubscriber Subscriber { get; }
    ILiveConfiguration<OutputInputProfile>? InputProfile { get; }
    IPluginConfigurationEditor? ConfigurationEditor => null;
    IApplicationControl? ApplicationControl => null;

    ILiveConfiguration<TConfig> ObserveConfiguration<TConfig>(
        Func<IConfiguration, TConfig> snapshotFactory,
        Func<TConfig, ConfigurationValidationResult>? validator = null)
        where TConfig : notnull;
}
public interface IPluginPublisher { ValueTask PublishAsync<T>(string providedPortId, T payload, CancellationToken cancellationToken = default) where T : IShrineMessage; }
public interface IPluginSubscriber
{
    IAsyncDisposable Subscribe<T>(
        string requiredPortId,
        Func<MessageEnvelope<T>, CancellationToken, ValueTask> handler)
        where T : IShrineMessage;

    IInputRouteMonitor ObserveRoute(string requiredPortId);
}
