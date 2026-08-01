using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopShrine.Runtime.Tests;

public sealed class RoutingCoordinatorTests
{
    [Fact]
    public void ActivityAndProfileEventsReevaluateSelectionImmediately()
    {
        var contracts = new ContractRegistry();
        contracts.RegisterAssembly(typeof(NowPlayingState).Assembly);
        var activities = new InputActivityRegistry();
        var ports = new PortRegistry(contracts, activities);
        var high = new StatefulInput("high");
        var low = new StatefulInput("low");
        ports.RegisterPlugin(high);
        ports.RegisterPlugin(low);
        ports.RegisterPlugin(new Output());
        var profile = new OutputInputProfile
        {
            OutputId = "output",
            Inputs =
            [
                Preference("high", 100),
                Preference("low", 1)
            ]
        };
        var profiles = new ProfileSource(profile);
        var routes = new RouteTable();
        using var coordinator = new InputRoutingCoordinator(
            ports,
            activities,
            profiles,
            new InputResolver(contracts),
            routes,
            NullLogger<InputRoutingCoordinator>.Instance);
        coordinator.Start();
        var consumer = new PortAddress("output", "media");

        Assert.Equal("high", routes.GetProviders(consumer).Single().PluginId);

        high.SetActivity(InputActivityState.Inactive);
        Assert.Equal("low", routes.GetProviders(consumer).Single().PluginId);

        high.SetActivity(InputActivityState.Active);
        Assert.Equal("high", routes.GetProviders(consumer).Single().PluginId);

        profiles.Set(profile with
        {
            Inputs = profile.Inputs
                .Select(x => x.InputId == "high"
                    ? x with { Enabled = false }
                    : x)
                .ToArray()
        });
        Assert.Equal("low", routes.GetProviders(consumer).Single().PluginId);
    }

    private static InputPreference Preference(string id, int priority) =>
        new()
        {
            InputId = id,
            Enabled = true,
            Priority = priority
        };

    private sealed class ProfileSource(OutputInputProfile initial) :
        IOutputInputProfileService
    {
        private readonly LiveConfiguration<OutputInputProfile> live =
            new(initial, null, NullLogger.Instance);
        private OutputInputProfile profile = initial;

        public event EventHandler<OutputProfilesChangedEventArgs>? Changed;
        public IReadOnlyCollection<OutputInputProfile> Profiles => [profile];

        public ILiveConfiguration<OutputInputProfile> GetLiveProfile(
            string outputId) =>
            live;

        public ValueTask SynchroniseAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> TryUpdateAsync(
            OutputInputProfile value,
            CancellationToken cancellationToken)
        {
            Set(value);
            return ValueTask.FromResult(true);
        }

        public void Set(OutputInputProfile value)
        {
            profile = value;
            live.TryUpdate(value);
            Changed?.Invoke(this, new([value.OutputId]));
        }
    }

    private sealed class StatefulInput(string id) : IInputPlugin
    {
        public PluginDescriptor Descriptor { get; } = new()
        {
            Id = id,
            Name = id,
            Version = new(1, 0)
        };

        public IReadOnlyCollection<ProvidedPortDescriptor> ProvidedPorts { get; } =
        [
            new()
            {
                PortId = "media",
                DisplayName = "Media",
                Contract = new(
                    "desktop-shrine.media.now-playing",
                    new(1, 2, 0))
            }
        ];

        public InputActivityState ActivityState { get; private set; } =
            InputActivityState.Active;

        public event EventHandler<InputActivityStateChangedEventArgs>?
            ActivityStateChanged;

        public void SetActivity(InputActivityState next)
        {
            var previous = ActivityState;
            ActivityState = next;
            ActivityStateChanged?.Invoke(this, new(previous, next));
        }

        public ValueTask InitialiseAsync(
            IPluginContext context,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask StartAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Output : IOutputPlugin
    {
        public PluginDescriptor Descriptor { get; } = new()
        {
            Id = "output",
            Name = "Output",
            Version = new(1, 0)
        };

        public IReadOnlyCollection<RequiredPortDescriptor> RequiredPorts { get; } =
        [
            new()
            {
                PortId = "media",
                DisplayName = "Media",
                Requirement = new(
                    "desktop-shrine.media.now-playing",
                    VersionRange.Between(new(1, 0, 0), new(2, 0, 0)))
            }
        ];

        public ValueTask InitialiseAsync(
            IPluginContext context,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask StartAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
