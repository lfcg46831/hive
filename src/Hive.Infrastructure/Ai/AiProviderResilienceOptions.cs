using System.Collections.Immutable;
using System.Globalization;
using Hive.Domain.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Ai;

/// <summary>Validated startup snapshot of operational provider policies.</summary>
public sealed class AiProviderResilienceOptions
{
    public const string SectionName = "Hive:AiGateway:Providers";

    public IReadOnlyDictionary<string, AiProviderResiliencePolicy> Policies { get; private set; } =
        ImmutableDictionary<string, AiProviderResiliencePolicy>.Empty;

    internal void UseSnapshot(AiProviderResilienceOptions snapshot) => Policies = snapshot.Policies;

    internal void Load(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        RequireObject(section);
        var policies = ImmutableDictionary.CreateBuilder<string, AiProviderResiliencePolicy>(
            StringComparer.Ordinal);
        foreach (var provider in section.GetChildren())
        {
            if (string.IsNullOrWhiteSpace(provider.Key) || provider.Key != provider.Key.Trim())
            {
                throw Invalid("invalid-provider-id");
            }

            policies.Add(provider.Key, ReadPolicy(provider));
        }

        Policies = policies.ToImmutable();
    }

    private static AiProviderResiliencePolicy ReadPolicy(IConfigurationSection provider)
    {
        RequireFields(provider, "RateLimit", "Queue", "Retry", "CircuitBreaker");
        var rate = provider.GetSection("RateLimit");
        var queue = provider.GetSection("Queue");
        var retry = provider.GetSection("Retry");
        var circuit = provider.GetSection("CircuitBreaker");
        RequireFields(rate, "MaxConcurrentCalls", "MaxCallsPerWindow", "Window");
        RequireFields(queue, "MaxDepth", "MaxWait");
        RequireFields(retry, "MaxAttempts", "InitialBackoff", "MaxBackoff", "JitterRatio");
        RequireFields(circuit, "SamplingWindow", "FailureThreshold", "OpenDuration",
            "HalfOpenMaxConcurrentProbes");
        var defaults = AiProviderResiliencePolicy.Default;

        try
        {
            var policy = new AiProviderResiliencePolicy(
                new AiProviderRateLimitPolicy(
                    Integer(rate, "MaxConcurrentCalls", defaults.RateLimit.MaxConcurrentCalls),
                    Integer(rate, "MaxCallsPerWindow", defaults.RateLimit.MaxCallsPerWindow),
                    Duration(rate, "Window", defaults.RateLimit.Window)),
                new AiProviderQueuePolicy(
                    Integer(queue, "MaxDepth", defaults.Queue.MaxDepth),
                    Duration(queue, "MaxWait", defaults.Queue.MaxWait)),
                new AiProviderRetryPolicy(
                    Integer(retry, "MaxAttempts", defaults.Retry.MaxAttempts),
                    Duration(retry, "InitialBackoff", defaults.Retry.InitialBackoff),
                    Duration(retry, "MaxBackoff", defaults.Retry.MaxBackoff),
                    Decimal(retry, "JitterRatio", defaults.Retry.JitterRatio)),
                new AiProviderCircuitBreakerPolicy(
                    Duration(circuit, "SamplingWindow", defaults.CircuitBreaker.SamplingWindow),
                    Integer(circuit, "FailureThreshold", defaults.CircuitBreaker.FailureThreshold),
                    Duration(circuit, "OpenDuration", defaults.CircuitBreaker.OpenDuration),
                    Integer(circuit, "HalfOpenMaxConcurrentProbes",
                        defaults.CircuitBreaker.HalfOpenMaxConcurrentProbes)));

            // These durations reach System.Threading.Timer / Task.Delay directly.
            var maxTimerDuration = TimeSpan.FromMilliseconds(4294967294);
            if (policy.Queue.MaxWait > maxTimerDuration ||
                policy.Retry.MaxBackoff > maxTimerDuration)
            {
                throw Invalid("invalid-range");
            }

            return policy;
        }
        catch (ArgumentException)
        {
            // Domain exceptions may contain ActualValue. Never retain the inner exception.
            throw Invalid("invalid-range");
        }
    }

    private static void RequireObject(IConfigurationSection section)
    {
        if (section.Value is not null)
        {
            throw Invalid("invalid-shape");
        }
    }

    private static void RequireFields(IConfigurationSection section, params string[] fields)
    {
        RequireObject(section);
        if (section.GetChildren().Any(child =>
            !fields.Contains(child.Key, StringComparer.OrdinalIgnoreCase)))
        {
            throw Invalid("unknown-field");
        }
    }

    private static string? Scalar(IConfigurationSection section, string name)
    {
        var child = section.GetSection(name);
        if (child.GetChildren().Any())
        {
            throw Invalid("invalid-shape");
        }

        // A present null leaf is different from an omitted setting.
        if (child.Value is null && section.GetChildren().Any(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw Invalid("invalid-value");
        }

        return child.Value;
    }

    private static int Integer(IConfigurationSection section, string name, int fallback)
    {
        var value = Scalar(section, name);
        if (value is null) return fallback;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result : throw Invalid("invalid-value");
    }

    private static decimal Decimal(IConfigurationSection section, string name, decimal fallback)
    {
        var value = Scalar(section, name);
        if (value is null) return fallback;
        return decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var result)
            ? result : throw Invalid("invalid-value");
    }

    private static TimeSpan Duration(IConfigurationSection section, string name, TimeSpan fallback)
    {
        var value = Scalar(section, name);
        if (value is null) return fallback;
        return TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out var result)
            ? result : throw Invalid("invalid-value");
    }

    private static OptionsValidationException Invalid(string reason) => new(
        Options.DefaultName,
        typeof(AiProviderResilienceOptions),
        new[] { $"AI gateway resilience configuration-invalid: {SectionName} ({reason})." });
}

internal sealed class ConfiguredAiProviderResiliencePolicyResolver(
    IOptions<AiProviderResilienceOptions> options) : IAiProviderResiliencePolicyResolver
{
    private readonly IReadOnlyDictionary<string, AiProviderResiliencePolicy> _policies =
        options.Value.Policies;

    public AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider) =>
        provider is not null && _policies.TryGetValue(provider.ProviderId, out var policy)
            ? policy : AiProviderResiliencePolicy.Default;
}
