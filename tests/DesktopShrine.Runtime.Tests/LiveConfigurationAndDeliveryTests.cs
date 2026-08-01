using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DesktopShrine.Runtime.Tests;

public sealed class LiveConfigurationAndDeliveryTests
{
    [Fact]
    public void PluginConfigurationUsesItsOwnFileAndHostOverrides()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"desktop-shrine-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "audio-collector.json"),
                """
                {
                  "CaptureMode": "loopback",
                  "FramesPerSecond": 50
                }
                """);
            var hostConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Plugins:audio-collector:FramesPerSecond"] = "30"
                })
                .Build();
            var provider = new PluginConfigurationProvider(
                hostConfiguration,
                Options.Create(new DesktopShrineOptions
                {
                    PluginConfigurationDirectory = directory
                }),
                NullLogger<PluginConfigurationProvider>.Instance);

            var pluginConfiguration = provider.GetConfiguration(
                "audio-collector");

            Assert.Equal("loopback", pluginConfiguration["CaptureMode"]);
            Assert.Equal("30", pluginConfiguration["FramesPerSecond"]);
            (pluginConfiguration as IDisposable)?.Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingPluginConfigurationCreatesNamedPlaceholder()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"desktop-shrine-config-{Guid.NewGuid():N}");
        try
        {
            var provider = new PluginConfigurationProvider(
                new ConfigurationBuilder().Build(),
                Options.Create(new DesktopShrineOptions
                {
                    PluginConfigurationDirectory = directory
                }),
                NullLogger<PluginConfigurationProvider>.Instance);

            var pluginConfiguration = provider.GetConfiguration(
                "new-output-plugin");

            var fileName = Path.Combine(
                directory,
                "new-output-plugin.json");
            Assert.True(File.Exists(fileName));
            Assert.Equal(
                "{}",
                File.ReadAllText(fileName).Trim());
            Assert.Empty(pluginConfiguration.GetChildren());
            (pluginConfiguration as IDisposable)?.Dispose();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidConfigurationIsAppliedWithoutRestartingOutput()
    {
        var live = new LiveConfiguration<OutputSettings>(
            new(25, "bars"),
            Validate,
            NullLogger.Instance);
        var output = new RunningOutput(live);
        output.Start();

        Assert.True(live.TryUpdate(new(80, "wave")));

        Assert.Equal(1, output.StartCount);
        Assert.Equal(2, output.ApplyCount);
        Assert.Equal(new(80, "wave"), output.Settings);
    }

    [Fact]
    public void InvalidConfigurationKeepsLastValidAtomicSnapshot()
    {
        var initial = new OutputSettings(25, "bars");
        var live = new LiveConfiguration<OutputSettings>(
            initial,
            Validate,
            NullLogger.Instance);
        var notifications = 0;
        live.Changed += (_, _) => notifications++;

        Assert.False(live.TryUpdate(new(101, "wave")));

        Assert.Same(initial, live.Current);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task AggregateRouteDeliversDataFromEveryEnabledProvider()
    {
        var routes = new RouteTable();
        var bus = new PortBus(routes, NullLogger<PortBus>.Instance);
        var consumer = new PortAddress("logger", "media");
        var first = new PortAddress("first", "media");
        var second = new PortAddress("second", "media");
        routes.ReplaceBindings(
        [
            new() { Provider = first, Consumer = consumer },
            new() { Provider = second, Consumer = consumer }
        ]);
        var received = new List<string>();
        await using var subscription = bus.Subscribe<NowPlayingState>(
            consumer,
            (message, _) =>
            {
                received.Add(message.Source.PluginId);
                return ValueTask.CompletedTask;
            });
        var contract = MediaContract();

        await bus.PublishAsync(
            first,
            contract,
            MediaState(),
            TestContext.Current.CancellationToken);
        await bus.PublishAsync(
            second,
            contract,
            MediaState(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], received);
    }

    [Fact]
    public async Task OldSelectedInputCannotPublishAfterRouteChanges()
    {
        var routes = new RouteTable();
        var bus = new PortBus(routes, NullLogger<PortBus>.Instance);
        var consumer = new PortAddress("display", "media");
        var oldSource = new PortAddress("old", "media");
        var newSource = new PortAddress("new", "media");
        routes.ReplaceBindings(
        [
            new() { Provider = oldSource, Consumer = consumer }
        ]);
        var received = new List<string>();
        await using var subscription = bus.Subscribe<NowPlayingState>(
            consumer,
            (message, _) =>
            {
                received.Add(message.Source.PluginId);
                return ValueTask.CompletedTask;
            });
        routes.ReplaceBindings(
        [
            new() { Provider = newSource, Consumer = consumer }
        ]);

        await bus.PublishAsync(
            oldSource,
            MediaContract(),
            MediaState(),
            TestContext.Current.CancellationToken);
        await bus.PublishAsync(
            newSource,
            MediaContract(),
            MediaState(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["new"], received);
    }

    [Fact]
    public void OutputIsNotifiedWhenSelectedInputChanges()
    {
        var routes = new RouteTable();
        var consumer = new PortAddress("display", "media");
        var first = new PortAddress("first", "media");
        var second = new PortAddress("second", "media");
        var monitor = routes.Observe(consumer, isExclusive: true);
        InputRouteSnapshot? notified = null;
        monitor.Changed += (_, args) => notified = args.Current;

        routes.ReplaceBindings(
        [
            new() { Provider = first, Consumer = consumer }
        ]);
        routes.ReplaceBindings(
        [
            new() { Provider = second, Consumer = consumer }
        ]);

        Assert.Equal(second, notified?.SelectedProvider);
    }

    [Fact]
    public void PluginHealthAndInputActivityAreIndependent()
    {
        var input = new InactiveInput();
        var activities = new InputActivityRegistry();
        activities.Register(input);
        var loaded = new LoadedPlugin
        {
            Instance = input,
            Manifest = new()
            {
                Id = input.Descriptor.Id,
                Version = "1.0.0",
                EntryAssembly = "unused.dll",
                EntryType = typeof(InactiveInput).FullName!
            },
            LoadContext = new(
                typeof(LiveConfigurationAndDeliveryTests).Assembly.Location,
                []),
            State = PluginLifecycleState.Running
        };

        Assert.Equal(PluginLifecycleState.Running, loaded.State);
        Assert.Equal(
            InputActivityState.Inactive,
            activities.Find(input.Descriptor.Id)?.State);
    }

    private static ConfigurationValidationResult Validate(OutputSettings value) =>
        value.Brightness is >= 0 and <= 100
            ? ConfigurationValidationResult.Success
            : ConfigurationValidationResult.Failure(
                "Brightness must be from 0 through 100.");

    private static ContractDescriptor MediaContract() =>
        new()
        {
            Reference = new(
                "desktop-shrine.media.now-playing",
                new(1, 2, 0)),
            PayloadType = typeof(NowPlayingState),
            DeliveryKind = DeliveryKind.State,
            PackageId = "desktop-shrine.contracts.media"
        };

    private static NowPlayingState MediaState() =>
        new() { IsAvailable = true, CapturedAt = DateTimeOffset.UtcNow };

    private sealed record OutputSettings(int Brightness, string Mode);

    private sealed class RunningOutput(
        ILiveConfiguration<OutputSettings> configuration)
    {
        public int StartCount { get; private set; }
        public int ApplyCount { get; private set; }
        public OutputSettings? Settings { get; private set; }

        public void Start()
        {
            StartCount++;
            Apply(configuration.Current);
            configuration.Changed += (_, args) => Apply(args.Current);
        }

        private void Apply(OutputSettings settings)
        {
            Settings = settings;
            ApplyCount++;
        }
    }

    private sealed class InactiveInput : IInputPlugin
    {
        public PluginDescriptor Descriptor { get; } = new()
        {
            Id = "inactive-but-healthy",
            Name = "Inactive but healthy",
            Version = new(1, 0)
        };

        public IReadOnlyCollection<ProvidedPortDescriptor> ProvidedPorts => [];
        public InputActivityState ActivityState => InputActivityState.Inactive;
        public event EventHandler<InputActivityStateChangedEventArgs>?
            ActivityStateChanged;

        public ValueTask InitialiseAsync(
            IPluginContext context,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask StartAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void MakeActive() =>
            ActivityStateChanged?.Invoke(
                this,
                new(
                    InputActivityState.Inactive,
                    InputActivityState.Active));
    }
}
