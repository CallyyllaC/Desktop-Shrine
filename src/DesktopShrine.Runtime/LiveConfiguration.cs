using DesktopShrine.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace DesktopShrine.Runtime;

public sealed class LiveConfiguration<TConfig> : ILiveConfiguration<TConfig>
    where TConfig : notnull
{
    private readonly Func<TConfig, ConfigurationValidationResult>? validator;
    private readonly ILogger logger;
    private object current;

    public LiveConfiguration(
        TConfig initial,
        Func<TConfig, ConfigurationValidationResult>? validator,
        ILogger logger)
    {
        this.validator = validator;
        this.logger = logger;
        var validation = Validate(initial);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                $"The initial configuration is invalid: {string.Join("; ", validation.Errors)}");
        current = initial;
    }

    public TConfig Current => (TConfig)Volatile.Read(ref current);

    public event EventHandler<ConfigurationChangedEventArgs<TConfig>>? Changed;

    public bool TryUpdate(TConfig next)
    {
        var validation = Validate(next);
        if (!validation.IsValid)
        {
            logger.LogError(
                "Rejected invalid configuration snapshot: {ValidationErrors}",
                string.Join("; ", validation.Errors));
            return false;
        }

        var previous = (TConfig)Interlocked.Exchange(ref current, next);
        var args = new ConfigurationChangedEventArgs<TConfig>(previous, next);
        foreach (EventHandler<ConfigurationChangedEventArgs<TConfig>> handler
                 in Changed?.GetInvocationList()
                     .Cast<EventHandler<ConfigurationChangedEventArgs<TConfig>>>()
                 ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "A configuration change listener failed");
            }
        }

        return true;
    }

    private ConfigurationValidationResult Validate(TConfig value)
    {
        if (validator is null)
            return ConfigurationValidationResult.Success;

        try
        {
            return validator(value);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Configuration validation failed");
            return ConfigurationValidationResult.Failure(exception.Message);
        }
    }
}

internal sealed class ConfigurationSectionMonitor<TConfig> :
    ILiveConfiguration<TConfig>,
    IDisposable
    where TConfig : notnull
{
    private readonly IConfiguration configuration;
    private readonly Func<IConfiguration, TConfig> snapshotFactory;
    private readonly LiveConfiguration<TConfig> live;
    private readonly ILogger logger;
    private readonly IDisposable reloadSubscription;

    public ConfigurationSectionMonitor(
        IConfiguration configuration,
        Func<IConfiguration, TConfig> snapshotFactory,
        Func<TConfig, ConfigurationValidationResult>? validator,
        ILogger logger)
    {
        this.configuration = configuration;
        this.snapshotFactory = snapshotFactory;
        this.logger = logger;
        live = new(snapshotFactory(configuration), validator, logger);
        reloadSubscription = ChangeToken.OnChange(
            configuration.GetReloadToken,
            Reload);
    }

    public TConfig Current => live.Current;

    public event EventHandler<ConfigurationChangedEventArgs<TConfig>>? Changed
    {
        add => live.Changed += value;
        remove => live.Changed -= value;
    }

    public void Dispose() => reloadSubscription.Dispose();

    private void Reload()
    {
        try
        {
            live.TryUpdate(snapshotFactory(configuration));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not create a configuration snapshot; the last valid snapshot remains active");
        }
    }
}
