using Hive.Domain.Ai;

namespace Hive.Tests;

public sealed class AiGatewayProtocolEnumTests
{
    [Fact]
    public void Finish_reasons_have_stable_values()
    {
        Assert.Equal(1, (int)AiFinishReason.Stop);
        Assert.Equal(2, (int)AiFinishReason.Length);
        Assert.Equal(3, (int)AiFinishReason.ToolCalls);
        Assert.Equal(4, (int)AiFinishReason.ContentFiltered);
        Assert.Equal(5, (int)AiFinishReason.Unknown);
        Assert.Equal(
            [
                AiFinishReason.Stop,
                AiFinishReason.Length,
                AiFinishReason.ToolCalls,
                AiFinishReason.ContentFiltered,
                AiFinishReason.Unknown,
            ],
            Enum.GetValues<AiFinishReason>());
    }

    [Theory]
    [InlineData(AiFinishReason.Stop, "stop")]
    [InlineData(AiFinishReason.Length, "length")]
    [InlineData(AiFinishReason.ToolCalls, "tool-calls")]
    [InlineData(AiFinishReason.ContentFiltered, "content-filtered")]
    [InlineData(AiFinishReason.Unknown, "unknown")]
    public void Finish_reason_wire_values_round_trip_canonically(
        AiFinishReason value,
        string wireValue)
    {
        Assert.Equal(wireValue, AiFinishReasonContract.ToWireValue(value));
        Assert.Equal(value, AiFinishReasonContract.ParseWireValue(wireValue));
        Assert.True(AiFinishReasonContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void Error_codes_have_stable_values()
    {
        Assert.Equal(1, (int)AiGatewayErrorCode.ConfigurationInvalid);
        Assert.Equal(2, (int)AiGatewayErrorCode.ProviderNotAuthorized);
        Assert.Equal(3, (int)AiGatewayErrorCode.ModelNotAuthorized);
        Assert.Equal(4, (int)AiGatewayErrorCode.ToolNotAuthorized);
        Assert.Equal(5, (int)AiGatewayErrorCode.BudgetInsufficient);
        Assert.Equal(6, (int)AiGatewayErrorCode.CredentialsMissing);
        Assert.Equal(7, (int)AiGatewayErrorCode.Timeout);
        Assert.Equal(8, (int)AiGatewayErrorCode.Canceled);
        Assert.Equal(9, (int)AiGatewayErrorCode.QuotaExceeded);
        Assert.Equal(10, (int)AiGatewayErrorCode.ProviderUnavailable);
        Assert.Equal(11, (int)AiGatewayErrorCode.ProviderRejected);
        Assert.Equal(12, (int)AiGatewayErrorCode.InvalidProviderResponse);
        Assert.Equal(13, (int)AiGatewayErrorCode.Unknown);
        Assert.Equal(
            [
                AiGatewayErrorCode.ConfigurationInvalid,
                AiGatewayErrorCode.ProviderNotAuthorized,
                AiGatewayErrorCode.ModelNotAuthorized,
                AiGatewayErrorCode.ToolNotAuthorized,
                AiGatewayErrorCode.BudgetInsufficient,
                AiGatewayErrorCode.CredentialsMissing,
                AiGatewayErrorCode.Timeout,
                AiGatewayErrorCode.Canceled,
                AiGatewayErrorCode.QuotaExceeded,
                AiGatewayErrorCode.ProviderUnavailable,
                AiGatewayErrorCode.ProviderRejected,
                AiGatewayErrorCode.InvalidProviderResponse,
                AiGatewayErrorCode.Unknown,
            ],
            Enum.GetValues<AiGatewayErrorCode>());
    }

    [Fact]
    public void Gateway_call_results_have_stable_values()
    {
        Assert.Equal(1, (int)AiGatewayCallResult.Succeeded);
        Assert.Equal(2, (int)AiGatewayCallResult.Failed);
        Assert.Equal(
            [
                AiGatewayCallResult.Succeeded,
                AiGatewayCallResult.Failed,
            ],
            Enum.GetValues<AiGatewayCallResult>());
    }

    [Fact]
    public void Processing_modes_have_stable_values()
    {
        Assert.Equal(1, (int)AiProcessingMode.Interactive);
        Assert.Equal(2, (int)AiProcessingMode.Batch);
        Assert.Equal(
            [
                AiProcessingMode.Interactive,
                AiProcessingMode.Batch,
            ],
            Enum.GetValues<AiProcessingMode>());
    }

    [Theory]
    [InlineData(AiProcessingMode.Interactive, "interactive")]
    [InlineData(AiProcessingMode.Batch, "batch")]
    public void Processing_mode_wire_values_round_trip_canonically(
        AiProcessingMode value,
        string wireValue)
    {
        Assert.Equal(wireValue, AiProcessingModeContract.ToWireValue(value));
        Assert.Equal(value, AiProcessingModeContract.ParseWireValue(wireValue));
        Assert.True(AiProcessingModeContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData(AiGatewayErrorCode.ConfigurationInvalid, "configuration-invalid")]
    [InlineData(AiGatewayErrorCode.ProviderNotAuthorized, "provider-not-authorized")]
    [InlineData(AiGatewayErrorCode.ModelNotAuthorized, "model-not-authorized")]
    [InlineData(AiGatewayErrorCode.ToolNotAuthorized, "tool-not-authorized")]
    [InlineData(AiGatewayErrorCode.BudgetInsufficient, "budget-insufficient")]
    [InlineData(AiGatewayErrorCode.CredentialsMissing, "credentials-missing")]
    [InlineData(AiGatewayErrorCode.Timeout, "timeout")]
    [InlineData(AiGatewayErrorCode.Canceled, "canceled")]
    [InlineData(AiGatewayErrorCode.QuotaExceeded, "quota-exceeded")]
    [InlineData(AiGatewayErrorCode.ProviderUnavailable, "provider-unavailable")]
    [InlineData(AiGatewayErrorCode.ProviderRejected, "provider-rejected")]
    [InlineData(AiGatewayErrorCode.InvalidProviderResponse, "invalid-provider-response")]
    [InlineData(AiGatewayErrorCode.Unknown, "unknown")]
    public void Error_code_wire_values_round_trip_canonically(
        AiGatewayErrorCode value,
        string wireValue)
    {
        Assert.Equal(wireValue, AiGatewayErrorCodeContract.ToWireValue(value));
        Assert.Equal(value, AiGatewayErrorCodeContract.ParseWireValue(wireValue));
        Assert.True(AiGatewayErrorCodeContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData(AiGatewayCallResult.Succeeded, "succeeded")]
    [InlineData(AiGatewayCallResult.Failed, "failed")]
    public void Gateway_call_result_wire_values_round_trip_canonically(
        AiGatewayCallResult value,
        string wireValue)
    {
        Assert.Equal(wireValue, AiGatewayCallResultContract.ToWireValue(value));
        Assert.Equal(value, AiGatewayCallResultContract.ParseWireValue(wireValue));
        Assert.True(AiGatewayCallResultContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }


    [Theory]
    [InlineData("")]
    [InlineData("tool-calls ")]
    [InlineData("ToolCalls")]
    [InlineData("tool_calls")]
    [InlineData("1")]
    public void Wire_parsing_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => AiFinishReasonContract.ParseWireValue(value));
        Assert.False(AiFinishReasonContract.TryParseWireValue(value, out var finishReason));
        Assert.Equal(default, finishReason);

        Assert.Throws<ArgumentException>(() => AiGatewayErrorCodeContract.ParseWireValue(value));
        Assert.False(AiGatewayErrorCodeContract.TryParseWireValue(value, out var errorCode));
        Assert.Equal(default, errorCode);

        Assert.Throws<ArgumentException>(() => AiProcessingModeContract.ParseWireValue(value));
        Assert.False(AiProcessingModeContract.TryParseWireValue(value, out var processingMode));
        Assert.Equal(default, processingMode);

        Assert.Throws<ArgumentException>(() => AiGatewayCallResultContract.ParseWireValue(value));
        Assert.False(AiGatewayCallResultContract.TryParseWireValue(value, out var result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void Operations_reject_undefined_in_memory_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiFinishReasonContract.RequireDefined((AiFinishReason)0, "finishReason"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiFinishReasonContract.ToWireValue((AiFinishReason)0));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiGatewayErrorCodeContract.RequireDefined((AiGatewayErrorCode)0, "code"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiGatewayErrorCodeContract.ToWireValue((AiGatewayErrorCode)0));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiProcessingModeContract.RequireDefined((AiProcessingMode)0, "processingMode"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiProcessingModeContract.ToWireValue((AiProcessingMode)0));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiGatewayCallResultContract.RequireDefined((AiGatewayCallResult)0, "result"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiGatewayCallResultContract.ToWireValue((AiGatewayCallResult)0));
    }
}
