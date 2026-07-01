using System.ClientModel;
using Hive.Domain.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAI;
using OpenAI.Chat;

namespace Hive.Infrastructure.Ai;

public static class AiGatewayServiceCollectionExtensions
{
    private const string AiGatewayProviderKey = "Hive:AiGateway:Provider";
    private const string StubProviderName = "stub";
    private const string RealProviderName = "real";
    private const string OpenAiProviderId = "openai";

    public static IServiceCollection AddHiveAiGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAiGatewayProvider, UnavailableAiGatewayProvider>();
        services.TryAddSingleton<IAiGatewayAuditPublisher>(
            _ => NoopAiGatewayAuditPublisher.Instance);
        services.TryAddSingleton<IAiGatewayDetailedAuditPublisher>(
            _ => NoopAiGatewayDetailedAuditPublisher.Instance);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IAiGateway, AiGateway>();

        return services;
    }

    public static IServiceCollection AddHiveAiGateway(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHiveAiGateway();

        if (string.Equals(
            configuration[AiGatewayProviderKey],
            StubProviderName,
            StringComparison.OrdinalIgnoreCase))
        {
            services.AddHiveAiGatewayStub(options =>
                configuration
                    .GetSection(StubAiGatewayProviderOptions.SectionName)
                    .Bind(options));
        }
        else if (string.Equals(
            configuration[AiGatewayProviderKey],
            RealProviderName,
            StringComparison.OrdinalIgnoreCase))
        {
            services.AddHiveAiGatewayReal(configuration);
        }

        return services;
    }

    public static IServiceCollection AddHiveAiGatewayStub(
        this IServiceCollection services,
        Action<StubAiGatewayProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHiveAiGateway();
        services.AddOptions<StubAiGatewayProviderOptions>();

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.Replace(ServiceDescriptor.Singleton<
            IAiGatewayProvider,
            StubAiGatewayProvider>());

        return services;
    }

    /// <summary>
    /// Registers the secure configuration binding and factory for the optional
    /// real AI gateway provider (US-F0-07-T05a). It binds
    /// <see cref="RealAiGatewayProviderOptions"/> and registers
    /// <see cref="IRealAiGatewayProviderFactory"/>. It does not activate the real
    /// provider in the gateway pipeline; the adapter (US-F0-07-T05b) and default
    /// activation (US-F0-07-T05c) are separate tasks.
    /// </summary>
    public static IServiceCollection AddHiveAiGatewayRealConfiguration(
        this IServiceCollection services,
        Action<RealAiGatewayProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<RealAiGatewayProviderOptions>();

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<
            IRealAiGatewayProviderFactory,
            RealAiGatewayProviderFactory>();

        return services;
    }

    public static IServiceCollection AddHiveAiGatewayRealConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddHiveAiGatewayRealConfiguration(options =>
            configuration
                .GetSection(RealAiGatewayProviderOptions.SectionName)
                .Bind(options));
    }

    /// <summary>
    /// Activates the real AI gateway adapter (US-F0-07-T05b) as the gateway
    /// provider. This is an explicit opt-in: it resolves the validated settings
    /// through <see cref="IRealAiGatewayProviderFactory"/> and wires
    /// <see cref="RealAiGatewayProvider"/> over an <see cref="IChatClient"/> that
    /// must already be registered. Constructing the concrete provider client and
    /// deciding default activation in the suite remain US-F0-07-T05c; misconfigured
    /// settings surface as a startup failure carrying the structured
    /// <see cref="AiGatewayErrorCode"/>.
    /// </summary>
    public static IServiceCollection AddHiveAiGatewayReal(
        this IServiceCollection services,
        Action<RealAiGatewayProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHiveAiGateway();
        services.AddHiveAiGatewayRealConfiguration(configure);

        services.Replace(ServiceDescriptor.Singleton<IAiGatewayProvider>(
            static provider =>
            {
                var settings = ResolveRealProviderSettings(provider);
                var chatClient = provider.GetRequiredService<IChatClient>();
                return new RealAiGatewayProvider(chatClient, settings);
            }));

        return services;
    }

    /// <summary>
    /// Explicitly activates the real provider from configuration (US-F0-07-T05c).
    /// This path constructs the first concrete <see cref="IChatClient"/> supported
    /// by the gateway: OpenAI via Microsoft.Extensions.AI.OpenAI. Construction is
    /// local-only; the provider is not called during registration.
    /// </summary>
    public static IServiceCollection AddHiveAiGatewayReal(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHiveAiGateway();
        services.AddHiveAiGatewayRealConfiguration(configuration);
        services.TryAddSingleton<IChatClient>(CreateOpenAiChatClient);

        services.Replace(ServiceDescriptor.Singleton<IAiGatewayProvider>(
            static provider =>
            {
                var settings = ResolveRealProviderSettings(provider);
                var chatClient = provider.GetRequiredService<IChatClient>();
                return new RealAiGatewayProvider(chatClient, settings);
            }));

        return services;
    }

    private static IChatClient CreateOpenAiChatClient(IServiceProvider provider)
    {
        var settings = ResolveRealProviderSettings(provider);
        if (!string.Equals(
            settings.DefaultProvider.ProviderId,
            OpenAiProviderId,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "AI gateway real provider is misconfigured " +
                $"({AiGatewayErrorCodeContract.ToWireValue(AiGatewayErrorCode.ConfigurationInvalid)}): " +
                $"unsupported real provider '{settings.DefaultProvider.ProviderId}'.");
        }

        var options = new OpenAIClientOptions();
        if (settings.Endpoint is { } endpoint)
        {
            options.Endpoint = endpoint;
        }

        var chatClient = new ChatClient(
            settings.DefaultProvider.ModelId,
            new ApiKeyCredential(settings.ApiKey),
            options);

        return chatClient.AsIChatClient();
    }

    private static RealAiGatewayProviderSettings ResolveRealProviderSettings(
        IServiceProvider provider)
    {
        var factory = provider.GetRequiredService<IRealAiGatewayProviderFactory>();
        var result = factory.ResolveSettings();
        if (result.IsSuccess)
        {
            return result.Settings!;
        }

        throw new InvalidOperationException(
            "AI gateway real provider is misconfigured " +
            $"({AiGatewayErrorCodeContract.ToWireValue(result.ErrorCode!.Value)}): " +
            result.FailureReason);
    }
}
