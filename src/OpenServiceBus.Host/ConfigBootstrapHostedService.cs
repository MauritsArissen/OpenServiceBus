using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Core.Configuration;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Host;

/// <summary>
/// At startup, declares everything listed in a Microsoft-emulator-compatible <c>config.json</c>:
/// queues, topics, subscriptions, and filter rules.
/// Resolution order:
///   1. <c>--config &lt;path&gt;</c> CLI argument
///   2. <c>OPENSERVICEBUS_CONFIG</c> environment variable
///   3. <c>config.json</c> in the current working directory (only if present)
/// </summary>
public sealed class ConfigBootstrapHostedService : IHostedService
{
    private const string ConfigFlag = "--config";
    private const string ConfigEnv = "OPENSERVICEBUS_CONFIG";
    private const string DefaultFileName = "config.json";

    private readonly IQueueRegistry _queues;
    private readonly ITopicRegistry? _topics;
    private readonly ILogger<ConfigBootstrapHostedService> _logger;
    private readonly IHostEnvironment _env;

    public ConfigBootstrapHostedService(
        IQueueRegistry queues,
        IHostEnvironment env,
        ILogger<ConfigBootstrapHostedService> logger,
        ITopicRegistry? topics = null)
    {
        _queues = queues;
        _topics = topics;
        _env = env;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var path = ResolveConfigPath();
        if (path is null)
        {
            _logger.LogInformation("No bootstrap config found ({Flag}, env {Env}, or {Default} in cwd). Starting with no pre-declared queues.",
                ConfigFlag, ConfigEnv, DefaultFileName);
            return;
        }

        _logger.LogInformation("Loading bootstrap config from {Path}", path);

        EmulatorConfigLoader.LoadResult result;
        try
        {
            result = EmulatorConfigLoader.LoadFromFile(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse bootstrap config at {Path}. Continuing without bootstrapping.", path);
            return;
        }

        foreach (var warning in result.Warnings)
        {
            _logger.LogWarning("config.json: {Warning}", warning);
        }

        foreach (var descriptor in result.Queues)
        {
            try
            {
                await _queues.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Bootstrapped queue '{Name}' (lockDuration={Lock}, maxDeliveryCount={Max}, ttl={Ttl}, sessions={Sessions}, dedup={Dedup}, forwardTo={Forward})",
                    descriptor.Name, descriptor.LockDuration, descriptor.MaxDeliveryCount,
                    descriptor.DefaultMessageTimeToLive?.ToString() ?? "(none)",
                    descriptor.RequiresSession, descriptor.RequiresDuplicateDetection,
                    descriptor.ForwardTo ?? "(none)");
            }
            catch (InvalidOperationException ex)
            {
                // A single bad queue (e.g. self-forward) must not abort startup - log and skip it,
                // matching how subscription and rule bootstrap already handle their failures.
                _logger.LogError(ex, "Failed to declare queue '{Name}' - skipping.", descriptor.Name);
            }
        }

        if (_topics is not null)
        {
            foreach (var topic in result.Topics)
            {
                try
                {
                    await _topics.CreateTopicAsync(topic, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Bootstrapped topic '{Name}' (ttl={Ttl})",
                        topic.Name, topic.DefaultMessageTimeToLive?.ToString() ?? "(none)");
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogError(ex, "Failed to declare topic '{Name}' - skipping.", topic.Name);
                }
            }

            foreach (var sub in result.Subscriptions)
            {
                try
                {
                    await _topics.CreateSubscriptionAsync(sub, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Bootstrapped subscription '{Topic}/{Name}' (sessions={Sessions}, forwardTo={Forward})",
                        sub.TopicName, sub.Name, sub.RequiresSession, sub.ForwardTo ?? "(none)");
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogError(ex, "Failed to declare subscription '{Topic}/{Name}'.", sub.TopicName, sub.Name);
                }
            }

            foreach (var rule in result.Rules)
            {
                try
                {
                    await _topics.CreateOrReplaceRuleAsync(rule, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Bootstrapped rule '{Topic}/{Sub}/{Name}' (filter={Filter})",
                        rule.TopicName, rule.SubscriptionName, rule.Name, rule.Filter.GetType().Name);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogError(ex, "Failed to declare rule '{Topic}/{Sub}/{Name}'.",
                        rule.TopicName, rule.SubscriptionName, rule.Name);
                }
            }
        }
        else if (result.Topics.Count > 0)
        {
            _logger.LogWarning(
                "config.json declares {Count} topic(s) but no ITopicRegistry is registered - skipping topics.",
                result.Topics.Count);
        }

        _logger.LogInformation(
            "Bootstrap complete: {Queues} queue(s), {Topics} topic(s), {Subs} subscription(s), {Rules} rule(s) declared.",
            result.Queues.Count, result.Topics.Count, result.Subscriptions.Count, result.Rules.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string? ResolveConfigPath()
    {
        // 1. CLI: --config <path>
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], ConfigFlag, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        // 2. Env var
        var envValue = Environment.GetEnvironmentVariable(ConfigEnv);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue;
        }

        // 3. Default: config.json in cwd
        var cwdCandidate = Path.Combine(_env.ContentRootPath, DefaultFileName);
        if (File.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }

        return null;
    }
}
