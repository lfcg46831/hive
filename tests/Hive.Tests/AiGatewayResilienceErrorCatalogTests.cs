using Hive.Domain.Ai;
using Hive.Domain.Identity;

namespace Hive.Tests;

public sealed class AiGatewayResilienceErrorCatalogTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("engineering");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly AiProviderMetadata Provider = new("openai", "gpt-5-mini");

    [Fact]
    public void Existing_error_construction_remains_compatible_and_reasonless()
    {
        var error = new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            AiGatewayErrorCode.Timeout,
            "The provider timed out.",
            isRetryable: true,
            Provider);

        Assert.Null(error.Reason);
    }

    [Fact]
    public void Existing_public_constructor_signatures_remain_available()
    {
        Assert.NotNull(typeof(AiGatewayError).GetConstructor(
        [
            typeof(OrganizationId),
            typeof(PositionId),
            typeof(ThreadId),
            typeof(MessageId),
            typeof(AiGatewayErrorCode),
            typeof(string),
            typeof(bool),
            typeof(AiProviderMetadata),
            typeof(AiGatewayFailureDiagnostics),
        ]));
        Assert.NotNull(typeof(AiGatewayAuditErrorSnapshot).GetConstructor(
        [
            typeof(AiGatewayErrorCode),
            typeof(string),
            typeof(bool),
            typeof(AiProviderMetadata),
            typeof(AiGatewayFailureDiagnostics),
        ]));
    }

    [Fact]
    public void Error_rejects_undefined_optional_reason()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            AiGatewayErrorCode.ProviderUnavailable,
            "Provider unavailable.",
            isRetryable: false,
            Provider,
            null,
            (AiGatewayErrorReason)0));
    }

    [Fact]
    public void Gateway_overloaded_is_retryable_and_does_not_expose_request_data()
    {
        var request = Request("Queue for jane@example.com with token=sk-secret123456789.");

        var error = AiGatewayResilienceErrorCatalog.GatewayOverloaded(request);

        Assert.Equal(Organization, error.OrganizationId);
        Assert.Equal(Position, error.PositionId);
        Assert.Equal(Thread, error.ThreadId);
        Assert.Equal(Message, error.MessageId);
        Assert.Equal(AiGatewayErrorCode.GatewayOverloaded, error.Code);
        Assert.Equal("AI gateway is overloaded.", error.Message);
        Assert.True(error.IsRetryable);
        Assert.Equal(Provider, error.Provider);
        Assert.Null(error.Diagnostics);
        Assert.Null(error.Reason);
        Assert.DoesNotContain("jane@example.com", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Circuit_open_prevents_same_provider_retry_and_has_canonical_reason()
    {
        var effectiveProvider = new AiProviderMetadata("anthropic", "claude-sonnet-4-5");

        var error = AiGatewayResilienceErrorCatalog.CircuitOpen(
            Request("Sensitive content"),
            effectiveProvider);

        Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, error.Code);
        Assert.Equal("AI provider circuit is open.", error.Message);
        Assert.False(error.IsRetryable);
        Assert.Equal(effectiveProvider, error.Provider);
        Assert.Equal(AiGatewayErrorReason.CircuitOpen, error.Reason);
    }

    [Fact]
    public void Fallback_exhausted_preserves_last_error_and_only_replaces_reason()
    {
        var diagnostics = new AiGatewayFailureDiagnostics(
            usage: new AiTokenUsage(10, 2, 12, isEstimated: false),
            providerStatusCode: 503);
        var lastError = new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            AiGatewayErrorCode.ProviderUnavailable,
            "AI provider circuit is open.",
            isRetryable: false,
            Provider,
            diagnostics,
            AiGatewayErrorReason.CircuitOpen);

        var error = AiGatewayResilienceErrorCatalog.FallbackExhausted(lastError);

        Assert.Equal(lastError.OrganizationId, error.OrganizationId);
        Assert.Equal(lastError.PositionId, error.PositionId);
        Assert.Equal(lastError.ThreadId, error.ThreadId);
        Assert.Equal(lastError.MessageId, error.MessageId);
        Assert.Equal(lastError.Code, error.Code);
        Assert.Equal(lastError.Message, error.Message);
        Assert.Equal(lastError.IsRetryable, error.IsRetryable);
        Assert.Same(lastError.Provider, error.Provider);
        Assert.Same(lastError.Diagnostics, error.Diagnostics);
        Assert.Equal(AiGatewayErrorReason.FallbackExhausted, error.Reason);
    }

    [Fact]
    public void Catalog_rejects_null_inputs()
    {
        Assert.Throws<ArgumentNullException>(
            () => AiGatewayResilienceErrorCatalog.GatewayOverloaded(null!));
        Assert.Throws<ArgumentNullException>(
            () => AiGatewayResilienceErrorCatalog.CircuitOpen(null!));
        Assert.Throws<ArgumentNullException>(
            () => AiGatewayResilienceErrorCatalog.FallbackExhausted(null!));
    }

    private static AiGatewayRequest Request(string content) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            content,
            provider: Provider);
}
