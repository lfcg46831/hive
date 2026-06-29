namespace Hive.Infrastructure.Ai;

public sealed class StubAiGatewayProviderOptions
{
    public const string SectionName = "Hive:AiGateway:Stub";

    public string ProviderId { get; set; } = "stub";

    public string ModelId { get; set; } = "deterministic";

    public string Outcome { get; set; } = "success";

    public string? Text { get; set; } = "Stub AI response.";

    public string FinishReason { get; set; } = "stop";

    public StubAiGatewayErrorOptions Error { get; set; } = new();

    public StubAiGatewayUsageOptions? Usage { get; set; }

    public StubAiGatewayCostOptions? Cost { get; set; }

    public StubAiGatewayToolCallOptions? ToolCall { get; set; }
}

public sealed class StubAiGatewayErrorOptions
{
    public string? Code { get; set; }

    public string? Message { get; set; }

    public bool? IsRetryable { get; set; }
}

public sealed class StubAiGatewayUsageOptions
{
    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public int? TotalTokens { get; set; }

    public bool IsEstimated { get; set; }
}

public sealed class StubAiGatewayCostOptions
{
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "EUR";

    public bool IsEstimated { get; set; }
}

public sealed class StubAiGatewayToolCallOptions
{
    public string Id { get; set; } = "stub-tool-call-1";

    public string Name { get; set; } = "stub.tool";

    public Dictionary<string, string> Arguments { get; set; } =
        new(StringComparer.Ordinal);
}
