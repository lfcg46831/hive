using System.Collections.Concurrent;
using Hive.Domain.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Ai;

/// <summary>Selects transport and credentials per effective attempt, after gateway policy validation.</summary>
internal sealed class RoutingRealAiGatewayProvider : IAiGatewayProvider, IDisposable
{
    private readonly IReadOnlyDictionary<string, RealAiGatewayProviderSettings> _settings;
    private readonly string _defaultProviderId;
    private readonly Func<RealAiGatewayProviderSettings, string, IChatClient> _createClient;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(string Provider, string Model), Lazy<Entry>> _clients = new();

    public RoutingRealAiGatewayProvider(
        RealAiGatewayProviderSettings defaultSettings,
        IEnumerable<RealAiGatewayProviderSettings> additionalSettings,
        Func<RealAiGatewayProviderSettings, string, IChatClient>? createClient = null,
        TimeProvider? timeProvider = null)
    {
        _defaultProviderId = defaultSettings.DefaultProvider.ProviderId;
        var settings = new Dictionary<string, RealAiGatewayProviderSettings>(StringComparer.Ordinal);
        foreach (var item in additionalSettings.Prepend(defaultSettings))
        {
            if (item.DefaultProvider.ProviderId is not ("openai" or "mistral") ||
                !settings.TryAdd(item.DefaultProvider.ProviderId, item))
            {
                throw InvalidConfiguration();
            }
        }

        _settings = settings;
        _createClient = createClient ?? RealAiChatClientFactory.Create;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<AiGatewayResponse> CompleteAsync(AiGatewayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = request.Provider?.ProviderId ?? _defaultProviderId;
        if (!_settings.TryGetValue(providerId, out var settings))
        {
            return Task.FromResult(AiGatewayResponse.Failed(new AiGatewayError(
                request.OrganizationId, request.PositionId, request.ThreadId, request.MessageId,
                AiGatewayErrorCode.ConfigurationInvalid,
                "AI gateway real provider is not configured.", false, request.Provider)));
        }

        var modelId = request.Provider?.ModelId ?? settings.DefaultProvider.ModelId;
        var entry = _clients.GetOrAdd((providerId, modelId), key => new Lazy<Entry>(() =>
        {
            var client = _createClient(settings, key.Model);
            return new Entry(client, new RealAiGatewayProvider(client, settings, _timeProvider));
        })).Value;
        return entry.Provider.CompleteAsync(request, cancellationToken);
    }

    internal static IEnumerable<RealAiGatewayProviderSettings> ReadAdditionalSettings(IConfiguration configuration)
    {
        foreach (var section in configuration.GetSection("Hive:AiGateway:RealProviders").GetChildren())
        {
            if (section.Key is not ("openai" or "mistral") || section.Value is not null)
            {
                throw InvalidConfiguration();
            }

            var options = new RealAiGatewayProviderOptions();
            try
            {
                section.Bind(options, binding => binding.ErrorOnUnknownConfiguration = true);
            }
            catch (InvalidOperationException)
            {
                throw InvalidConfiguration();
            }

            if (options.ProviderId is not null && options.ProviderId != section.Key)
            {
                throw InvalidConfiguration();
            }

            options.ProviderId = section.Key;
            var result = new RealAiGatewayProviderFactory(Options.Create(options)).ResolveSettings();
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    "AI gateway additional real provider is misconfigured (" +
                    AiGatewayErrorCodeContract.ToWireValue(result.ErrorCode!.Value) + ").");
            }

            yield return result.Settings!;
        }
    }

    private static InvalidOperationException InvalidConfiguration() => new(
        "AI gateway real provider is misconfigured (configuration-invalid): unsupported or duplicate provider configuration.");

    public void Dispose()
    {
        foreach (var entry in _clients.Values)
        {
            if (entry.IsValueCreated) entry.Value.Client.Dispose();
        }
    }

    private sealed record Entry(IChatClient Client, RealAiGatewayProvider Provider);
}
