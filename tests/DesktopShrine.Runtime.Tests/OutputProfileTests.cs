using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DesktopShrine.Runtime.Tests;

public sealed class OutputProfileTests
{
    [Fact]
    public async Task JsonStorePersistsCompleteProfileAtomically()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "desktop-shrine-profile-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "profiles.json");
            var store = new JsonOutputInputProfileStore(
                Options.Create(new DesktopShrineOptions
                {
                    OutputProfileFile = path
                }));
            OutputInputProfile profile = new()
            {
                OutputId = "output",
                Inputs =
                [
                    new()
                    {
                        InputId = "input",
                        Enabled = false,
                        Priority = 42
                    }
                ]
            };

            await store.SaveAsync(
                [profile],
                TestContext.Current.CancellationToken);
            var loaded = await store.LoadAsync(
                TestContext.Current.CancellationToken);

            Assert.Equal(profile.OutputId, loaded.Single().OutputId);
            Assert.Equal(
                profile.Inputs.ToArray(),
                loaded.Single().Inputs.ToArray());
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TwoOutputsKeepIndependentProfiles()
    {
        var fixture = new ProfileFixture();
        fixture.Register(new TestInput("input-a"));
        fixture.Register(new TestInput("input-b"));
        fixture.Register(new TestOutput("output-a"));
        fixture.Register(new TestOutput("output-b"));
        await fixture.Service.SynchroniseAsync(TestContext.Current.CancellationToken);

        var outputA = fixture.Service.Profiles.Single(x => x.OutputId == "output-a");
        var changedA = outputA with
        {
            Inputs = outputA.Inputs
                .Select(x => x.InputId == "input-a"
                    ? x with { Enabled = false, Priority = 99 }
                    : x)
                .ToArray()
        };
        Assert.True(await fixture.Service.TryUpdateAsync(
            changedA,
            TestContext.Current.CancellationToken));

        var outputB = fixture.Service.Profiles.Single(x => x.OutputId == "output-b");
        Assert.True(outputB.Inputs.Single(x => x.InputId == "input-a").Enabled);
        Assert.NotEqual(
            99,
            outputB.Inputs.Single(x => x.InputId == "input-a").Priority);
    }

    [Fact]
    public async Task NewlyDiscoveredInputIsAddedWithoutOverwritingExistingChoices()
    {
        var fixture = new ProfileFixture();
        fixture.Register(new TestInput("input-a"));
        fixture.Register(new TestOutput("output"));
        await fixture.Service.SynchroniseAsync(TestContext.Current.CancellationToken);

        var original = fixture.Service.Profiles.Single();
        var customised = original with
        {
            Inputs =
            [
                original.Inputs.Single() with
                {
                    Enabled = false,
                    Priority = 42
                }
            ]
        };
        Assert.True(await fixture.Service.TryUpdateAsync(
            customised,
            TestContext.Current.CancellationToken));

        fixture.Register(new TestInput("input-b"));
        await fixture.Service.SynchroniseAsync(TestContext.Current.CancellationToken);

        var updated = fixture.Service.Profiles.Single();
        var existing = updated.Inputs.Single(x => x.InputId == "input-a");
        Assert.False(existing.Enabled);
        Assert.Equal(42, existing.Priority);
        Assert.True(updated.Inputs.Single(x => x.InputId == "input-b").Enabled);
        Assert.True(fixture.Store.SaveCount >= 3);
    }

    [Fact]
    public async Task HighestPlacementInputIsAddedAboveExistingChoices()
    {
        var fixture = new ProfileFixture();
        fixture.Register(new TestInput("existing"));
        fixture.Register(new TestOutput("output"));
        await fixture.Service.SynchroniseAsync(
            TestContext.Current.CancellationToken);

        var original = fixture.Service.Profiles.Single();
        var customised = original with
        {
            Inputs =
            [
                original.Inputs.Single() with { Priority = 42 }
            ]
        };
        Assert.True(await fixture.Service.TryUpdateAsync(
            customised,
            TestContext.Current.CancellationToken));

        fixture.Register(new TestInput(
            "preferred",
            DefaultInputPriorityPlacement.Highest));
        await fixture.Service.SynchroniseAsync(
            TestContext.Current.CancellationToken);

        var updated = fixture.Service.Profiles.Single();
        Assert.Equal(
            42,
            updated.Inputs.Single(x => x.InputId == "existing").Priority);
        Assert.True(
            updated.Inputs.Single(x => x.InputId == "preferred").Priority
                > updated.Inputs.Single(x => x.InputId == "existing").Priority);
    }

    [Fact]
    public async Task InvalidProfilePreservesLastValidLiveSnapshot()
    {
        var fixture = new ProfileFixture();
        fixture.Register(new TestInput("input"));
        fixture.Register(new TestOutput("output"));
        await fixture.Service.SynchroniseAsync(TestContext.Current.CancellationToken);
        var live = fixture.Service.GetLiveProfile("output");
        var previous = live.Current;
        var changed = 0;
        live.Changed += (_, _) => changed++;

        var accepted = await fixture.Service.TryUpdateAsync(
            previous with
            {
                Inputs =
                [
                    previous.Inputs.Single(),
                    previous.Inputs.Single() with { Priority = 99 }
                ]
            },
            TestContext.Current.CancellationToken);

        Assert.False(accepted);
        Assert.Same(previous, live.Current);
        Assert.Equal(0, changed);
    }

    private sealed class ProfileFixture
    {
        private readonly ContractRegistry contracts = new();
        private readonly InputActivityRegistry activities =
            new();

        public ProfileFixture()
        {
            contracts.RegisterAssembly(typeof(NowPlayingState).Assembly);
            Ports = new(contracts, activities);
            Store = new();
            Service = new(
                Store,
                Ports,
                contracts,
                NullLogger<OutputInputProfileService>.Instance);
        }

        public PortRegistry Ports { get; }
        public InMemoryProfileStore Store { get; }
        public OutputInputProfileService Service { get; }

        public void Register(IShrinePlugin plugin) => Ports.RegisterPlugin(plugin);
    }

    private sealed class InMemoryProfileStore : IOutputInputProfileStore
    {
        private OutputInputProfile[] profiles = [];

        public int SaveCount { get; private set; }

        public ValueTask<IReadOnlyCollection<OutputInputProfile>> LoadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyCollection<OutputInputProfile>>(profiles);

        public ValueTask SaveAsync(
            IReadOnlyCollection<OutputInputProfile> value,
            CancellationToken cancellationToken)
        {
            profiles = value.ToArray();
            SaveCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestInput(
        string id,
        DefaultInputPriorityPlacement placement =
            DefaultInputPriorityPlacement.Lowest) : IInputPlugin
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

        public DefaultInputPriorityPlacement DefaultPriorityPlacement =>
            placement;

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

    private sealed class TestOutput(string id) : IOutputPlugin
    {
        public PluginDescriptor Descriptor { get; } = new()
        {
            Id = id,
            Name = id,
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
