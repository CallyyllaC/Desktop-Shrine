using System.Collections.Concurrent;
using DesktopShrine.Abstractions;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Runtime;

public sealed record InputActivitySnapshot(
    string InputId,
    InputActivityState State);

public sealed class InputActivityChangedEventArgs(
    InputActivitySnapshot previous,
    InputActivitySnapshot current) : EventArgs
{
    public InputActivitySnapshot Previous { get; } = previous;
    public InputActivitySnapshot Current { get; } = current;
}

public interface IInputActivityRegistry
{
    event EventHandler<InputActivityChangedEventArgs>? Changed;
    IReadOnlyCollection<InputActivitySnapshot> Inputs { get; }
    void Register(IInputPlugin input);
    void Unregister(string inputId);
    InputActivitySnapshot? Find(string inputId);
}

public sealed class InputActivityRegistry : IInputActivityRegistry
{
    private sealed record Registration(
        IInputPlugin Plugin,
        InputActivitySnapshot Snapshot);

    private readonly Dictionary<string, Registration> registrations =
        new(StringComparer.Ordinal);
    private readonly object gate = new();

    public event EventHandler<InputActivityChangedEventArgs>? Changed;

    public IReadOnlyCollection<InputActivitySnapshot> Inputs
    {
        get
        {
            lock (gate)
                return registrations.Values.Select(x => x.Snapshot).ToArray();
        }
    }

    public void Register(IInputPlugin input)
    {
        InputActivitySnapshot? previous = null;
        InputActivitySnapshot current;
        lock (gate)
        {
            if (registrations.Remove(input.Descriptor.Id, out var existing))
            {
                existing.Plugin.ActivityStateChanged -= OnActivityStateChanged;
                previous = existing.Snapshot;
            }

            current = new(
                input.Descriptor.Id,
                input.ActivityState);
            registrations.Add(input.Descriptor.Id, new(input, current));
            input.ActivityStateChanged += OnActivityStateChanged;
        }

        Changed?.Invoke(
            this,
            new(
                previous ?? current with { State = InputActivityState.Inactive },
                current));
    }

    public void Unregister(string inputId)
    {
        InputActivitySnapshot? previous;
        lock (gate)
        {
            if (!registrations.Remove(inputId, out var registration))
                return;
            registration.Plugin.ActivityStateChanged -= OnActivityStateChanged;
            previous = registration.Snapshot;
        }

        Changed?.Invoke(
            this,
            new(
                previous,
                previous with
                {
                    State = InputActivityState.Inactive
                }));
    }

    public InputActivitySnapshot? Find(string inputId)
    {
        lock (gate)
            return registrations.TryGetValue(inputId, out var registration)
                ? registration.Snapshot
                : null;
    }

    private void OnActivityStateChanged(
        object? sender,
        InputActivityStateChangedEventArgs args)
    {
        if (sender is not IInputPlugin input)
            return;

        InputActivitySnapshot previous;
        InputActivitySnapshot current;
        lock (gate)
        {
            if (!registrations.TryGetValue(input.Descriptor.Id, out var registration)
                || !ReferenceEquals(registration.Plugin, input))
                return;

            previous = registration.Snapshot;
            current = new(
                input.Descriptor.Id,
                args.Current);
            registrations[input.Descriptor.Id] = registration with
            {
                Snapshot = current
            };
        }

        Changed?.Invoke(this, new(previous, current));
    }
}

public interface IInputResolver
{
    RegisteredProvidedPort? ResolveExclusive(
        RegisteredRequiredPort consumer,
        IReadOnlyCollection<RegisteredProvidedPort> providers,
        OutputInputProfile profile,
        IReadOnlyCollection<InputActivitySnapshot> activities);

    IReadOnlyCollection<RegisteredProvidedPort> ResolveAggregate(
        RegisteredRequiredPort consumer,
        IReadOnlyCollection<RegisteredProvidedPort> providers,
        OutputInputProfile profile);
}

public sealed class InputResolver(IContractRegistry contracts) : IInputResolver
{
    public RegisteredProvidedPort? ResolveExclusive(
        RegisteredRequiredPort consumer,
        IReadOnlyCollection<RegisteredProvidedPort> providers,
        OutputInputProfile profile,
        IReadOnlyCollection<InputActivitySnapshot> activities)
    {
        var preferences = profile.Inputs
            .Where(x => x.Enabled)
            .ToDictionary(x => x.InputId, StringComparer.Ordinal);
        var states = activities.ToDictionary(x => x.InputId, StringComparer.Ordinal);
        var candidates = Compatible(consumer, providers)
            .Where(x => preferences.ContainsKey(x.Address.PluginId))
            .Where(x => states.ContainsKey(x.Address.PluginId))
            .Select(x => new
            {
                Provider = x,
                Preference = preferences[x.Address.PluginId],
                Activity = states[x.Address.PluginId]
            })
            .ToArray();

        return candidates
            .Where(x => x.Activity.State == InputActivityState.Active)
            .OrderByDescending(x => x.Preference.Priority)
            .ThenBy(x => x.Provider.Address.PluginId, StringComparer.Ordinal)
            .ThenBy(x => x.Provider.Address.PortId, StringComparer.Ordinal)
            .Select(x => x.Provider)
            .FirstOrDefault();
    }

    public IReadOnlyCollection<RegisteredProvidedPort> ResolveAggregate(
        RegisteredRequiredPort consumer,
        IReadOnlyCollection<RegisteredProvidedPort> providers,
        OutputInputProfile profile)
    {
        var enabled = profile.Inputs
            .Where(x => x.Enabled)
            .Select(x => x.InputId)
            .ToHashSet(StringComparer.Ordinal);
        return Compatible(consumer, providers)
            .Where(x => enabled.Contains(x.Address.PluginId))
            .OrderBy(x => x.Address.PluginId, StringComparer.Ordinal)
            .ThenBy(x => x.Address.PortId, StringComparer.Ordinal)
            .ToArray();
    }

    private IEnumerable<RegisteredProvidedPort> Compatible(
        RegisteredRequiredPort consumer,
        IEnumerable<RegisteredProvidedPort> providers) =>
        providers.Where(x =>
            contracts.IsCompatible(
                x.Descriptor.Contract,
                consumer.Descriptor.Requirement));

}

public sealed class RouteTableChangedEventArgs(
    IReadOnlyCollection<PortBinding> previous,
    IReadOnlyCollection<PortBinding> current) : EventArgs
{
    public IReadOnlyCollection<PortBinding> Previous { get; } = previous;
    public IReadOnlyCollection<PortBinding> Current { get; } = current;
}

public interface IRouteTable
{
    event EventHandler<RouteTableChangedEventArgs>? Changed;
    IReadOnlyCollection<PortBinding> Bindings { get; }
    void ReplaceBindings(IEnumerable<PortBinding> bindings);
    IReadOnlyCollection<PortAddress> GetConsumers(PortAddress provider);
    IReadOnlyCollection<PortAddress> GetProviders(PortAddress consumer);
    bool IsBound(PortAddress provider, PortAddress consumer);
    IInputRouteMonitor Observe(PortAddress consumer, bool isExclusive);
}

public sealed class RouteTable : IRouteTable
{
    private readonly ConcurrentDictionary<
        (PortAddress Consumer, bool Exclusive),
        RouteMonitor> monitors = [];
    private PortBinding[] bindings = [];

    public event EventHandler<RouteTableChangedEventArgs>? Changed;

    public IReadOnlyCollection<PortBinding> Bindings => Volatile.Read(ref bindings);

    public void ReplaceBindings(IEnumerable<PortBinding> value)
    {
        var next = value
            .Where(x => x.IsEnabled)
            .Distinct()
            .OrderBy(x => x.Consumer.PluginId, StringComparer.Ordinal)
            .ThenBy(x => x.Consumer.PortId, StringComparer.Ordinal)
            .ThenBy(x => x.Provider.PluginId, StringComparer.Ordinal)
            .ThenBy(x => x.Provider.PortId, StringComparer.Ordinal)
            .ToArray();
        var previous = Volatile.Read(ref bindings);
        if (previous.SequenceEqual(next))
            return;

        Volatile.Write(ref bindings, next);
        foreach (var monitor in monitors.Values)
            monitor.Update(CreateSnapshot(monitor.Consumer, monitor.IsExclusive));
        Changed?.Invoke(this, new(previous, next));
    }

    public IReadOnlyCollection<PortAddress> GetConsumers(PortAddress provider) =>
        Bindings
            .Where(x => x.Provider == provider)
            .Select(x => x.Consumer)
            .ToArray();

    public IReadOnlyCollection<PortAddress> GetProviders(PortAddress consumer) =>
        Bindings
            .Where(x => x.Consumer == consumer)
            .Select(x => x.Provider)
            .ToArray();

    public bool IsBound(PortAddress provider, PortAddress consumer) =>
        Bindings.Any(x => x.Provider == provider && x.Consumer == consumer);

    public IInputRouteMonitor Observe(PortAddress consumer, bool isExclusive) =>
        monitors.GetOrAdd(
            (consumer, isExclusive),
            key => new RouteMonitor(
                key.Consumer,
                key.Exclusive,
                CreateSnapshot(key.Consumer, key.Exclusive)));

    private InputRouteSnapshot CreateSnapshot(
        PortAddress consumer,
        bool isExclusive) =>
        new()
        {
            Consumer = consumer,
            IsExclusive = isExclusive,
            Providers = GetProviders(consumer).ToArray()
        };

    private sealed class RouteMonitor(
        PortAddress consumer,
        bool isExclusive,
        InputRouteSnapshot initial) : IInputRouteMonitor
    {
        private object current = initial;

        public PortAddress Consumer { get; } = consumer;
        public bool IsExclusive { get; } = isExclusive;
        public InputRouteSnapshot Current =>
            (InputRouteSnapshot)Volatile.Read(ref current);

        public event EventHandler<
            ConfigurationChangedEventArgs<InputRouteSnapshot>>? Changed;

        public void Update(InputRouteSnapshot next)
        {
            var previous =
                (InputRouteSnapshot)Interlocked.Exchange(ref current, next);
            if (previous.Providers.SequenceEqual(next.Providers))
                return;
            Changed?.Invoke(this, new(previous, next));
        }
    }
}

public interface IInputRoutingCoordinator : IDisposable
{
    void Start();
    void Reevaluate();
}

public sealed class InputRoutingCoordinator : IInputRoutingCoordinator
{
    private readonly IPortRegistry ports;
    private readonly IInputActivityRegistry activities;
    private readonly IOutputInputProfileService profiles;
    private readonly IInputResolver resolver;
    private readonly IRouteTable routes;
    private readonly ILogger<InputRoutingCoordinator> logger;
    private readonly object gate = new();
    private bool started;
    private bool disposed;

    public InputRoutingCoordinator(
        IPortRegistry ports,
        IInputActivityRegistry activities,
        IOutputInputProfileService profiles,
        IInputResolver resolver,
        IRouteTable routes,
        ILogger<InputRoutingCoordinator> logger)
    {
        this.ports = ports;
        this.activities = activities;
        this.profiles = profiles;
        this.resolver = resolver;
        this.routes = routes;
        this.logger = logger;
    }

    public void Start()
    {
        lock (gate)
        {
            if (started)
                return;
            started = true;
            ports.Changed += OnChanged;
            activities.Changed += OnActivityChanged;
            profiles.Changed += OnProfilesChanged;
        }

        Reevaluate();
    }

    public void Reevaluate()
    {
        lock (gate)
        {
            if (!started || disposed)
                return;

            try
            {
                var providers = ports.ProvidedPorts;
                var activitySnapshots = activities.Inputs;
                var profilesByOutput = profiles.Profiles.ToDictionary(
                    x => x.OutputId,
                    StringComparer.Ordinal);
                var bindings = new List<PortBinding>();

                foreach (var consumer in ports.RequiredPorts)
                {
                    if (!profilesByOutput.TryGetValue(
                            consumer.Address.PluginId,
                            out var profile))
                        continue;

                    var selected = consumer.Descriptor.Cardinality
                        == BindingCardinality.Multiple
                        ? resolver.ResolveAggregate(
                            consumer,
                            providers,
                            profile)
                        : AsCollection(
                            resolver.ResolveExclusive(
                                consumer,
                                providers,
                                profile,
                                activitySnapshots));
                    bindings.AddRange(selected.Select(provider => new PortBinding
                    {
                        Provider = provider.Address,
                        Consumer = consumer.Address
                    }));
                }

                routes.ReplaceBindings(bindings);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not reevaluate input routes");
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            ports.Changed -= OnChanged;
            activities.Changed -= OnActivityChanged;
            profiles.Changed -= OnProfilesChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs args) => Reevaluate();

    private void OnActivityChanged(
        object? sender,
        InputActivityChangedEventArgs args) =>
        Reevaluate();

    private void OnProfilesChanged(
        object? sender,
        OutputProfilesChangedEventArgs args) =>
        Reevaluate();

    private static IReadOnlyCollection<RegisteredProvidedPort> AsCollection(
        RegisteredProvidedPort? provider) =>
        provider is null ? [] : [provider];
}

internal interface IPortBus
{
    ValueTask PublishAsync<T>(
        PortAddress source,
        ContractDescriptor contract,
        T payload,
        CancellationToken cancellationToken)
        where T : IShrineMessage;

    IAsyncDisposable Subscribe<T>(
        PortAddress consumer,
        Func<MessageEnvelope<T>, CancellationToken, ValueTask> handler)
        where T : IShrineMessage;
}

internal sealed class PortBus : IPortBus
{
    private readonly IRouteTable routes;
    private readonly ILogger<PortBus> logger;
    private readonly ConcurrentDictionary<PortAddress, object> latest = [];
    private readonly ConcurrentDictionary<PortAddress, List<object>> handlers = [];

    public PortBus(IRouteTable routes, ILogger<PortBus> logger)
    {
        this.routes = routes;
        this.logger = logger;
        routes.Changed += OnRoutesChanged;
    }

    public async ValueTask PublishAsync<T>(
        PortAddress source,
        ContractDescriptor contract,
        T payload,
        CancellationToken cancellationToken)
        where T : IShrineMessage
    {
        var envelope = new MessageEnvelope<T>
        {
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Source = source,
            Contract = contract.Reference,
            Payload = payload
        };
        if (contract.DeliveryKind == DeliveryKind.State)
            latest[source] = envelope;

        foreach (var consumer in routes.GetConsumers(source))
        {
            object[] copy;
            lock (handlers)
                copy = handlers.TryGetValue(consumer, out var list)
                    ? [.. list]
                    : [];
            foreach (var handler in copy
                         .OfType<Func<
                             MessageEnvelope<T>,
                             CancellationToken,
                             ValueTask>>())
            {
                // A route may have changed after publication began.
                if (!routes.IsBound(source, consumer))
                    continue;
                await Invoke(
                    consumer,
                    envelope,
                    handler,
                    cancellationToken);
            }
        }
    }

    public IAsyncDisposable Subscribe<T>(
        PortAddress consumer,
        Func<MessageEnvelope<T>, CancellationToken, ValueTask> handler)
        where T : IShrineMessage
    {
        lock (handlers)
            handlers.GetOrAdd(consumer, _ => []).Add(handler);
        foreach (var provider in routes.GetProviders(consumer))
            Replay(provider, consumer);

        return new Subscription(() =>
        {
            lock (handlers)
                if (handlers.TryGetValue(consumer, out var list))
                    list.Remove(handler);
        });
    }

    private void OnRoutesChanged(
        object? sender,
        RouteTableChangedEventArgs args)
    {
        var previous = args.Previous.ToHashSet();
        foreach (var binding in args.Current.Where(x => !previous.Contains(x)))
            Replay(binding.Provider, binding.Consumer);
    }

    private void Replay(PortAddress provider, PortAddress consumer)
    {
        if (!latest.TryGetValue(provider, out var value)
            || !routes.IsBound(provider, consumer))
            return;

        object[] copy;
        lock (handlers)
            copy = handlers.TryGetValue(consumer, out var list)
                ? [.. list]
                : [];
        foreach (var handler in copy)
            _ = ReplayDynamic(provider, consumer, value, handler);
    }

    private async Task ReplayDynamic(
        PortAddress provider,
        PortAddress consumer,
        object envelope,
        object handler)
    {
        if (!routes.IsBound(provider, consumer))
            return;

        try
        {
            await ((dynamic)handler)((dynamic)envelope, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "State replay failed for {Consumer}",
                consumer);
        }
    }

    private async ValueTask Invoke<T>(
        PortAddress consumer,
        MessageEnvelope<T> envelope,
        Func<MessageEnvelope<T>, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken)
        where T : IShrineMessage
    {
        try
        {
            await handler(envelope, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Subscriber {Consumer} failed",
                consumer);
        }
    }

    private sealed class Subscription(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class PluginPublisher(
    string pluginId,
    IPortRegistry ports,
    IContractRegistry contracts,
    IPortBus bus) : IPluginPublisher
{
    public ValueTask PublishAsync<T>(
        string providedPortId,
        T payload,
        CancellationToken cancellationToken = default)
        where T : IShrineMessage
    {
        var port = ports.GetProvided(new(pluginId, providedPortId));
        var contract = contracts.GetRequired(port.Descriptor.Contract);
        if (contract.PayloadType != typeof(T) || payload.GetType() != typeof(T))
            throw new InvalidOperationException(
                "Payload CLR type does not match the declared contract.");
        return bus.PublishAsync(
            port.Address,
            contract,
            payload,
            cancellationToken);
    }
}

internal sealed class PluginSubscriber(
    string pluginId,
    IPortRegistry ports,
    IContractRegistry contracts,
    IPortBus bus,
    IRouteTable routes) : IPluginSubscriber
{
    public IAsyncDisposable Subscribe<T>(
        string requiredPortId,
        Func<MessageEnvelope<T>, CancellationToken, ValueTask> handler)
        where T : IShrineMessage
    {
        var port = ports.GetRequired(new(pluginId, requiredPortId));
        var contract = contracts.Find(
            port.Descriptor.Requirement.ContractId,
            port.Descriptor.Requirement.AcceptedVersions);
        if (contract?.PayloadType != typeof(T))
            throw new InvalidOperationException(
                "Subscription CLR type does not match contract.");
        return bus.Subscribe(port.Address, handler);
    }

    public IInputRouteMonitor ObserveRoute(string requiredPortId)
    {
        var port = ports.GetRequired(new(pluginId, requiredPortId));
        return routes.Observe(
            port.Address,
            port.Descriptor.Cardinality == BindingCardinality.Single);
    }
}
