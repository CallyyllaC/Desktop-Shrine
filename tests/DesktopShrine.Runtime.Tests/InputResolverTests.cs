using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Runtime;
using Xunit;

namespace DesktopShrine.Runtime.Tests;

public sealed class InputResolverTests
{
    private readonly InputResolver resolver;
    private readonly RegisteredRequiredPort consumer = Consumer();
    private readonly RegisteredProvidedPort first = Provider("first");
    private readonly RegisteredProvidedPort second = Provider("second");

    public InputResolverTests()
    {
        var contracts = new ContractRegistry();
        contracts.RegisterAssembly(typeof(NowPlayingState).Assembly);
        resolver = new(contracts);
    }

    [Fact]
    public void HighestPriorityActiveInputIsSelected()
    {
        var result = Resolve(
            Preference("first", priority: 10),
            Preference("second", priority: 20),
            Activity("first", InputActivityState.Active),
            Activity("second", InputActivityState.Active));

        Assert.Equal(second.Address, result?.Address);
    }

    [Fact]
    public void DisabledInputIsIgnored()
    {
        var result = Resolve(
            Preference("first", priority: 100, enabled: false),
            Preference("second", priority: 1),
            Activity("first", InputActivityState.Active),
            Activity("second", InputActivityState.Active));

        Assert.Equal(second.Address, result?.Address);
    }

    [Fact]
    public void InactiveInputIsIgnored()
    {
        var result = Resolve(
            Preference("first", priority: 100),
            Preference("second", priority: 1),
            Activity("first", InputActivityState.Inactive),
            Activity("second", InputActivityState.Active));

        Assert.Equal(second.Address, result?.Address);
    }

    [Fact]
    public void SelectionFallsBackWhenCurrentInputBecomesInactive()
    {
        var preferences = new[]
        {
            Preference("first", priority: 100),
            Preference("second", priority: 1)
        };
        var initial = Resolve(
            preferences,
            [
                Activity("first", InputActivityState.Active),
                Activity("second", InputActivityState.Active)
            ]);
        var fallback = Resolve(
            preferences,
            [
                Activity("first", InputActivityState.Inactive),
                Activity("second", InputActivityState.Active)
            ]);

        Assert.Equal(first.Address, initial?.Address);
        Assert.Equal(second.Address, fallback?.Address);
    }

    [Fact]
    public void SelectionSwitchesWhenHigherPriorityInputBecomesActive()
    {
        var preferences = new[]
        {
            Preference("first", priority: 100),
            Preference("second", priority: 1)
        };
        var initial = Resolve(
            preferences,
            [
                Activity("first", InputActivityState.Inactive),
                Activity("second", InputActivityState.Active)
            ]);
        var switched = Resolve(
            preferences,
            [
                Activity("first", InputActivityState.Active),
                Activity("second", InputActivityState.Active)
            ]);

        Assert.Equal(second.Address, initial?.Address);
        Assert.Equal(first.Address, switched?.Address);
    }

    [Fact]
    public void AggregateResolutionReturnsEveryEnabledCompatibleInput()
    {
        var profile = Profile(
            Preference("first", priority: 1),
            Preference("second", priority: 2));

        var result = resolver.ResolveAggregate(
            consumer with
            {
                Descriptor = consumer.Descriptor with
                {
                    Cardinality = BindingCardinality.Multiple
                }
            },
            [first, second],
            profile);

        Assert.Equal(
            [first.Address, second.Address],
            result.Select(x => x.Address));
    }

    private RegisteredProvidedPort? Resolve(
        InputPreference firstPreference,
        InputPreference secondPreference,
        InputActivitySnapshot firstActivity,
        InputActivitySnapshot secondActivity) =>
        Resolve(
            [firstPreference, secondPreference],
            [firstActivity, secondActivity]);

    private RegisteredProvidedPort? Resolve(
        IReadOnlyList<InputPreference> preferences,
        IReadOnlyCollection<InputActivitySnapshot> activities) =>
        resolver.ResolveExclusive(
            consumer,
            [first, second],
            Profile(preferences),
            activities);

    private static OutputInputProfile Profile(
        params IReadOnlyList<InputPreference> preferences) =>
        new() { OutputId = "output", Inputs = preferences };

    private static InputPreference Preference(
        string id,
        int priority,
        bool enabled = true) =>
        new()
        {
            InputId = id,
            Enabled = enabled,
            Priority = priority
        };

    private static InputActivitySnapshot Activity(
        string id,
        InputActivityState state) =>
        new(id, state);

    private static RegisteredProvidedPort Provider(string inputId) =>
        new(
            new(inputId, "media"),
            new()
            {
                PortId = "media",
                DisplayName = "Media",
                Contract = new(
                    "desktop-shrine.media.now-playing",
                    new(1, 2, 0))
            });

    private static RegisteredRequiredPort Consumer() =>
        new(
            new("output", "media"),
            new()
            {
                PortId = "media",
                DisplayName = "Media",
                Requirement = new(
                    "desktop-shrine.media.now-playing",
                    VersionRange.Between(new(1, 0, 0), new(2, 0, 0)))
            });
}
