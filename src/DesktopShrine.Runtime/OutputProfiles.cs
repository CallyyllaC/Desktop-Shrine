using System.Text.Json;
using DesktopShrine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DesktopShrine.Runtime;

public interface IOutputInputProfileStore
{
    ValueTask<IReadOnlyCollection<OutputInputProfile>> LoadAsync(
        CancellationToken cancellationToken);

    ValueTask SaveAsync(
        IReadOnlyCollection<OutputInputProfile> profiles,
        CancellationToken cancellationToken);
}

public sealed class JsonOutputInputProfileStore(
    IOptions<DesktopShrineOptions> options) : IOutputInputProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async ValueTask<IReadOnlyCollection<OutputInputProfile>> LoadAsync(
        CancellationToken cancellationToken)
    {
        var path = options.Value.OutputProfileFile;
        if (!File.Exists(path))
            return [];

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<OutputInputProfile[]>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    public async ValueTask SaveAsync(
        IReadOnlyCollection<OutputInputProfile> profiles,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(options.Value.OutputProfileFile);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    profiles,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

public sealed class OutputProfilesChangedEventArgs(
    IReadOnlyCollection<string> outputIds) : EventArgs
{
    public IReadOnlyCollection<string> OutputIds { get; } = outputIds;
}

public interface IOutputInputProfileService
{
    event EventHandler<OutputProfilesChangedEventArgs>? Changed;
    IReadOnlyCollection<OutputInputProfile> Profiles { get; }
    ILiveConfiguration<OutputInputProfile> GetLiveProfile(string outputId);
    ValueTask SynchroniseAsync(CancellationToken cancellationToken);
    ValueTask<bool> TryUpdateAsync(
        OutputInputProfile profile,
        CancellationToken cancellationToken);
}

public sealed class OutputInputProfileService :
    IOutputInputProfileService,
    IDisposable
{
    private readonly IOutputInputProfileStore store;
    private readonly IPortRegistry ports;
    private readonly IContractRegistry contracts;
    private readonly ILogger<OutputInputProfileService> logger;
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private readonly Dictionary<string, LiveConfiguration<OutputInputProfile>> live =
        new(StringComparer.Ordinal);
    private OutputInputProfile[] profiles = [];
    private bool loaded;
    private bool disposed;

    public OutputInputProfileService(
        IOutputInputProfileStore store,
        IPortRegistry ports,
        IContractRegistry contracts,
        ILogger<OutputInputProfileService> logger)
    {
        this.store = store;
        this.ports = ports;
        this.contracts = contracts;
        this.logger = logger;
        ports.Changed += OnPortsChanged;
    }

    public event EventHandler<OutputProfilesChangedEventArgs>? Changed;

    public IReadOnlyCollection<OutputInputProfile> Profiles =>
        Volatile.Read(ref profiles);

    public ILiveConfiguration<OutputInputProfile> GetLiveProfile(string outputId)
    {
        lock (live)
            return live.TryGetValue(outputId, out var value)
                ? value
                : throw new KeyNotFoundException(
                    $"No input profile exists for output {outputId}.");
    }

    public async ValueTask SynchroniseAsync(CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            if (!loaded)
            {
                var loadedProfiles = await store.LoadAsync(cancellationToken);
                var validationErrors = loadedProfiles
                    .SelectMany(Validate)
                    .ToArray();
                if (validationErrors.Length > 0)
                    throw new InvalidDataException(
                        $"Stored output profiles are invalid: {string.Join("; ", validationErrors)}");

                profiles = loadedProfiles
                    .Select(Clone)
                    .OrderBy(x => x.OutputId, StringComparer.Ordinal)
                    .ToArray();
                loaded = true;
            }

            var updated = MergeMissingEntries(profiles);
            var changedIds = FindChangedOutputIds(profiles, updated);
            if (changedIds.Count == 0)
            {
                EnsureLiveConfigurations(updated);
                return;
            }

            await store.SaveAsync(updated, cancellationToken);
            Publish(updated, changedIds);
        }
        finally
        {
            updateGate.Release();
        }
    }

    public async ValueTask<bool> TryUpdateAsync(
        OutputInputProfile profile,
        CancellationToken cancellationToken)
    {
        var candidate = Clone(profile);
        var errors = Validate(candidate).ToArray();
        if (errors.Length > 0)
        {
            logger.LogError(
                "Rejected invalid input profile for {OutputId}: {ValidationErrors}",
                profile.OutputId,
                string.Join("; ", errors));
            return false;
        }

        await updateGate.WaitAsync(cancellationToken);
        try
        {
            if (!loaded)
                throw new InvalidOperationException(
                    "Output profiles must be synchronised before they can be updated.");

            if (!ports.RequiredPorts.Any(x => x.Address.PluginId == candidate.OutputId))
            {
                logger.LogError(
                    "Rejected input profile for unknown output {OutputId}",
                    candidate.OutputId);
                return false;
            }

            var proposed = profiles
                .Where(x => x.OutputId != candidate.OutputId)
                .Append(candidate)
                .OrderBy(x => x.OutputId, StringComparer.Ordinal)
                .ToArray();
            var next = MergeMissingEntries(proposed);
            var normalisedCandidate =
                next.Single(x => x.OutputId == candidate.OutputId);
            var existing = profiles.Single(x => x.OutputId == candidate.OutputId);
            if (ProfileEquals(existing, normalisedCandidate))
                return true;

            await store.SaveAsync(next, cancellationToken);
            Publish(next, [candidate.OutputId]);
            return true;
        }
        finally
        {
            updateGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        ports.Changed -= OnPortsChanged;
        updateGate.Dispose();
    }

    private OutputInputProfile[] MergeMissingEntries(
        IReadOnlyCollection<OutputInputProfile> current)
    {
        var byOutput = current.ToDictionary(x => x.OutputId, Clone, StringComparer.Ordinal);
        var providers = ports.ProvidedPorts;

        foreach (var outputGroup in ports.RequiredPorts.GroupBy(x => x.Address.PluginId))
        {
            if (!byOutput.TryGetValue(outputGroup.Key, out var profile))
            {
                profile = new()
                {
                    OutputId = outputGroup.Key,
                    Inputs = []
                };
            }

            var compatibleInputs = providers
                .Where(provider => outputGroup.Any(required =>
                    contracts.IsCompatible(
                        provider.Descriptor.Contract,
                        required.Descriptor.Requirement)))
                .GroupBy(
                    provider => provider.Address.PluginId,
                    StringComparer.Ordinal)
                .Select(group => new
                {
                    InputId = group.Key,
                    Placement = group.Any(provider =>
                        provider.DefaultPriorityPlacement
                            == DefaultInputPriorityPlacement.Highest)
                        ? DefaultInputPriorityPlacement.Highest
                        : DefaultInputPriorityPlacement.Lowest
                })
                .OrderBy(x => x.InputId, StringComparer.Ordinal)
                .ToArray();

            var preferences = profile.Inputs.ToList();
            var existingIds = preferences
                .Select(x => x.InputId)
                .ToHashSet(StringComparer.Ordinal);
            var nextLowestPriority = preferences.Count == 0
                ? 0
                : Decrement(preferences.Min(x => x.Priority));
            var nextHighestPriority = preferences.Count == 0
                ? 1
                : Increment(preferences.Max(x => x.Priority));

            foreach (var input in compatibleInputs.Where(
                         x => !existingIds.Contains(x.InputId)))
            {
                var priority =
                    input.Placement == DefaultInputPriorityPlacement.Highest
                        ? nextHighestPriority
                        : nextLowestPriority;
                preferences.Add(new()
                {
                    InputId = input.InputId,
                    Enabled = true,
                    Priority = priority
                });
                if (input.Placement == DefaultInputPriorityPlacement.Highest)
                    nextHighestPriority = Increment(nextHighestPriority);
                else
                    nextLowestPriority = Decrement(nextLowestPriority);
            }

            byOutput[outputGroup.Key] = profile with { Inputs = preferences.ToArray() };
        }

        return byOutput.Values
            .OrderBy(x => x.OutputId, StringComparer.Ordinal)
            .Select(Clone)
            .ToArray();
    }

    private void EnsureLiveConfigurations(
        IEnumerable<OutputInputProfile> values)
    {
        lock (live)
        {
            foreach (var profile in values)
                if (!live.ContainsKey(profile.OutputId))
                    live.Add(
                        profile.OutputId,
                        new(profile, ValidateResult, logger));
        }
    }

    private void Publish(
        OutputInputProfile[] next,
        IReadOnlyCollection<string> changedIds)
    {
        EnsureLiveConfigurations(next);
        Volatile.Write(ref profiles, next);

        lock (live)
            foreach (var outputId in changedIds)
            {
                var profile = next.Single(x => x.OutputId == outputId);
                live[outputId].TryUpdate(profile);
            }

        Changed?.Invoke(this, new(changedIds));
    }

    private void OnPortsChanged(object? sender, EventArgs args)
    {
        if (!loaded || disposed)
            return;

        _ = SynchroniseAfterDiscoveryAsync();
    }

    private async Task SynchroniseAfterDiscoveryAsync()
    {
        try
        {
            await SynchroniseAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not synchronise output profiles after plugin discovery changed");
        }
    }

    private static OutputInputProfile Clone(OutputInputProfile profile) =>
        profile with
        {
            Inputs = profile.Inputs.Select(x => x with { }).ToArray()
        };

    private static IReadOnlyCollection<string> FindChangedOutputIds(
        IReadOnlyCollection<OutputInputProfile> previous,
        IReadOnlyCollection<OutputInputProfile> current)
    {
        var old = previous.ToDictionary(x => x.OutputId, StringComparer.Ordinal);
        return current
            .Where(profile =>
                !old.TryGetValue(profile.OutputId, out var existing)
                || !ProfileEquals(existing, profile))
            .Select(x => x.OutputId)
            .ToArray();
    }

    private static bool ProfileEquals(
        OutputInputProfile left,
        OutputInputProfile right) =>
        left.OutputId == right.OutputId
        && left.Inputs.SequenceEqual(right.Inputs);

    private static int Decrement(int value) =>
        value == int.MinValue ? int.MinValue : value - 1;

    private static int Increment(int value) =>
        value == int.MaxValue ? int.MaxValue : value + 1;

    private static ConfigurationValidationResult ValidateResult(
        OutputInputProfile profile)
    {
        var errors = Validate(profile).ToArray();
        return errors.Length == 0
            ? ConfigurationValidationResult.Success
            : ConfigurationValidationResult.Failure(errors);
    }

    private static IEnumerable<string> Validate(OutputInputProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.OutputId))
            yield return "OutputId is required.";
        if (profile.Inputs is null)
        {
            yield return "Inputs is required.";
            yield break;
        }

        var duplicateIds = profile.Inputs
            .Where(x => !string.IsNullOrWhiteSpace(x.InputId))
            .GroupBy(x => x.InputId, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key);
        foreach (var duplicateId in duplicateIds)
            yield return $"InputId '{duplicateId}' occurs more than once.";

        foreach (var input in profile.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.InputId))
                yield return "InputId is required.";
        }
    }
}
