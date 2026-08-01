using System.Reflection;
using DesktopShrine.Abstractions;

namespace DesktopShrine.Runtime;
public interface IContractRegistry { IReadOnlyCollection<ContractDescriptor> Contracts { get; } void RegisterAssembly(Assembly assembly); ContractDescriptor GetRequired(ContractReference reference); ContractDescriptor? Find(string contractId, VersionRange acceptedVersions); bool IsCompatible(ContractReference provided, ContractRequirement required); }
public sealed class ContractRegistry : IContractRegistry
{
    private readonly Dictionary<(string, Version), ContractDescriptor> contracts = [];
    public IReadOnlyCollection<ContractDescriptor> Contracts => contracts.Values.ToArray();
    public void RegisterAssembly(Assembly assembly) { var package = assembly.GetCustomAttribute<ShrineContractPackageAttribute>() ?? throw new InvalidOperationException("Assembly is not a contract package."); foreach (var type in assembly.GetTypes()) { var a = type.GetCustomAttribute<ShrineContractAttribute>(); if (a is null) continue; var expected = a.DeliveryKind switch { DeliveryKind.Event => typeof(IShrineEvent), DeliveryKind.State => typeof(IShrineState), _ => typeof(IShrineStreamFrame) }; if (!expected.IsAssignableFrom(type)) throw new InvalidOperationException($"Contract {type} has an inconsistent delivery marker."); var descriptor = new ContractDescriptor { Reference = new(a.ContractId, a.Version), PayloadType = type, DeliveryKind = a.DeliveryKind, PackageId = package.PackageId }; var key = (a.ContractId, a.Version); if (contracts.TryGetValue(key, out var old) && old.PayloadType != type) throw new InvalidOperationException($"Conflicting contract {a.ContractId} {a.Version}."); contracts[key] = descriptor; } }
    public ContractDescriptor GetRequired(ContractReference r) => contracts.TryGetValue((r.ContractId, r.Version), out var value) ? value : throw new KeyNotFoundException($"Unknown contract {r.ContractId} {r.Version}.");
    public ContractDescriptor? Find(string id, VersionRange range) => contracts.Values.SingleOrDefault(x => x.Reference.ContractId == id && range.Contains(x.Reference.Version));
    public bool IsCompatible(ContractReference p, ContractRequirement r) => p.ContractId == r.ContractId && r.AcceptedVersions.Contains(p.Version);
}
public sealed record RegisteredProvidedPort(
    PortAddress Address,
    ProvidedPortDescriptor Descriptor,
    DefaultInputPriorityPlacement DefaultPriorityPlacement =
        DefaultInputPriorityPlacement.Lowest);
public sealed record RegisteredRequiredPort(PortAddress Address, RequiredPortDescriptor Descriptor);
public interface IPortRegistry
{
    event EventHandler? Changed;
    void RegisterPlugin(IShrinePlugin plugin);
    void UnregisterPlugin(string pluginId);
    IReadOnlyCollection<RegisteredProvidedPort> ProvidedPorts { get; }
    IReadOnlyCollection<RegisteredRequiredPort> RequiredPorts { get; }
    RegisteredProvidedPort GetProvided(PortAddress address);
    RegisteredRequiredPort GetRequired(PortAddress address);
}

public sealed class PortRegistry(
    IContractRegistry contracts,
    IInputActivityRegistry? activities = null) : IPortRegistry
{
    private readonly Dictionary<PortAddress, RegisteredProvidedPort> provided = [];
    private readonly Dictionary<PortAddress, RegisteredRequiredPort> required = [];
    private readonly object gate = new();

    public event EventHandler? Changed;

    public IReadOnlyCollection<RegisteredProvidedPort> ProvidedPorts
    {
        get { lock (gate) return provided.Values.ToArray(); }
    }

    public IReadOnlyCollection<RegisteredRequiredPort> RequiredPorts
    {
        get { lock (gate) return required.Values.ToArray(); }
    }

    public void RegisterPlugin(IShrinePlugin plugin)
    {
        lock (gate)
        {
            if (plugin is IInputPlugin input)
            {
                foreach (var descriptor in input.ProvidedPorts)
                {
                    contracts.GetRequired(descriptor.Contract);
                    var address = new PortAddress(plugin.Descriptor.Id, descriptor.PortId);
                    provided.Add(
                        address,
                        new(
                            address,
                            descriptor,
                            input.DefaultPriorityPlacement));
                }

                activities?.Register(input);
            }

            if (plugin is IOutputPlugin output)
            {
                foreach (var descriptor in output.RequiredPorts)
                {
                    if (contracts.Find(
                            descriptor.Requirement.ContractId,
                            descriptor.Requirement.AcceptedVersions) is null)
                        throw new InvalidOperationException(
                            $"Unknown required contract {descriptor.Requirement.ContractId}.");

                    var address = new PortAddress(plugin.Descriptor.Id, descriptor.PortId);
                    required.Add(address, new(address, descriptor));
                }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UnregisterPlugin(string pluginId)
    {
        var changed = false;
        lock (gate)
        {
            foreach (var address in provided.Keys.Where(x => x.PluginId == pluginId).ToArray())
                changed |= provided.Remove(address);
            foreach (var address in required.Keys.Where(x => x.PluginId == pluginId).ToArray())
                changed |= required.Remove(address);
            activities?.Unregister(pluginId);
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public RegisteredProvidedPort GetProvided(PortAddress address)
    {
        lock (gate)
            return provided.TryGetValue(address, out var value)
                ? value
                : throw new KeyNotFoundException($"Undeclared provided port {address}.");
    }

    public RegisteredRequiredPort GetRequired(PortAddress address)
    {
        lock (gate)
            return required.TryGetValue(address, out var value)
                ? value
                : throw new KeyNotFoundException($"Undeclared required port {address}.");
    }
}
