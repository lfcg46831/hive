using System.Text.Json;
using System.Text.Json.Serialization;
using Hive.Domain.Ai;
using Hive.Domain.Identity;

namespace Hive.Actors.Serialization;

/// <summary>
/// Converters for the AI gateway protocol (US-F1-05-T07). Enums render as their canonical wire
/// values (§9.5) and the records whose shape System.Text.Json cannot bind on its own — a private
/// constructor, or two public overloads — are written and read field by field, so the domain stays
/// free of any serialization concern.
/// </summary>
internal sealed class AiProcessingModeJsonConverter : WireEnumJsonConverter<AiProcessingMode>
{
    protected override string ToWire(AiProcessingMode value) =>
        AiProcessingModeContract.ToWireValue(value);

    protected override bool TryParseWire(string? value, out AiProcessingMode result) =>
        AiProcessingModeContract.TryParseWireValue(value, out result);
}

internal sealed class AiFinishReasonJsonConverter : WireEnumJsonConverter<AiFinishReason>
{
    protected override string ToWire(AiFinishReason value) =>
        AiFinishReasonContract.ToWireValue(value);

    protected override bool TryParseWire(string? value, out AiFinishReason result) =>
        AiFinishReasonContract.TryParseWireValue(value, out result);
}

internal sealed class AiOutputConstraintModeJsonConverter :
    WireEnumJsonConverter<AiOutputConstraintMode>
{
    protected override string ToWire(AiOutputConstraintMode value) =>
        AiOutputConstraintModeContract.ToWireValue(value);

    protected override bool TryParseWire(string? value, out AiOutputConstraintMode result) =>
        AiOutputConstraintModeContract.TryParseWireValue(value, out result);
}

internal sealed class AiGatewayErrorCodeJsonConverter : WireEnumJsonConverter<AiGatewayErrorCode>
{
    protected override string ToWire(AiGatewayErrorCode value) =>
        AiGatewayErrorCodeContract.ToWireValue(value);

    protected override bool TryParseWire(string? value, out AiGatewayErrorCode result) =>
        AiGatewayErrorCodeContract.TryParseWireValue(value, out result);
}

internal sealed class AiGatewayErrorReasonJsonConverter :
    WireEnumJsonConverter<AiGatewayErrorReason>
{
    protected override string ToWire(AiGatewayErrorReason value) =>
        AiGatewayErrorReasonContract.ToWireValue(value);

    protected override bool TryParseWire(string? value, out AiGatewayErrorReason result) =>
        AiGatewayErrorReasonContract.TryParseWireValue(value, out result);
}

/// <summary>
/// Wire contract for the context-message role. The role has no domain-level wire contract, so the
/// closed set is pinned here and undefined values are rejected instead of silently widened.
/// </summary>
internal sealed class AiGatewayMessageRoleJsonConverter :
    WireEnumJsonConverter<AiGatewayMessageRole>
{
    protected override string ToWire(AiGatewayMessageRole value) => value switch
    {
        AiGatewayMessageRole.System => "system",
        AiGatewayMessageRole.User => "user",
        AiGatewayMessageRole.Assistant => "assistant",
        AiGatewayMessageRole.Tool => "tool",
        _ => throw new JsonException("AiGatewayMessageRole has an undefined value."),
    };

    protected override bool TryParseWire(string? value, out AiGatewayMessageRole result)
    {
        switch (value)
        {
            case "system":
                result = AiGatewayMessageRole.System;
                return true;
            case "user":
                result = AiGatewayMessageRole.User;
                return true;
            case "assistant":
                result = AiGatewayMessageRole.Assistant;
                return true;
            case "tool":
                result = AiGatewayMessageRole.Tool;
                return true;
            default:
                result = default;
                return false;
        }
    }
}

/// <summary>Field-by-field converter for the policy, which exposes two public constructors.</summary>
internal sealed class AiGatewayPolicyJsonConverter : JsonConverter<AiGatewayPolicy>
{
    public override AiGatewayPolicy Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        return new AiGatewayPolicy(
            AiJsonElements.Require<IReadOnlyList<AiProviderMetadata>>(
                root,
                "authorizedModels",
                options),
            AiJsonElements.ReadBoolean(root, "hasAvailableBudget", defaultValue: true),
            AiJsonElements.Read<int?>(root, "maxOutputTokens", options),
            AiJsonElements.Read<TimeSpan?>(root, "maxTimeout", options),
            AiJsonElements.Read<IReadOnlyList<AiProcessingMode>>(
                root,
                "allowedProcessingModes",
                options),
            AiJsonElements.Read<IReadOnlyList<string>>(root, "authorizedTools", options),
            AiJsonElements.Read<IReadOnlyList<AiProviderMetadata>>(root, "fallback", options));
    }

    public override void Write(
        Utf8JsonWriter writer,
        AiGatewayPolicy value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        AiJsonElements.Write(writer, "authorizedModels", value.AuthorizedModels, options);
        writer.WriteBoolean("hasAvailableBudget", value.HasAvailableBudget);
        AiJsonElements.Write(writer, "maxOutputTokens", value.MaxOutputTokens, options);
        AiJsonElements.Write(writer, "maxTimeout", value.MaxTimeout, options);
        AiJsonElements.Write(writer, "allowedProcessingModes", value.AllowedProcessingModes, options);
        AiJsonElements.Write(writer, "authorizedTools", value.AuthorizedTools, options);
        AiJsonElements.Write(writer, "fallback", value.Fallback, options);
        writer.WriteEndObject();
    }
}

/// <summary>Field-by-field converter for the error, which exposes two public constructors.</summary>
internal sealed class AiGatewayErrorJsonConverter : JsonConverter<AiGatewayError>
{
    public override AiGatewayError Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var organizationId = AiJsonElements.Require<OrganizationId>(root, "organizationId", options);
        var positionId = AiJsonElements.Require<PositionId>(root, "positionId", options);
        var threadId = AiJsonElements.Require<ThreadId>(root, "threadId", options);
        var messageId = AiJsonElements.Require<MessageId>(root, "messageId", options);
        var code = AiJsonElements.Require<AiGatewayErrorCode>(root, "code", options);
        var message = AiJsonElements.Require<string>(root, "message", options);
        var isRetryable = AiJsonElements.ReadBoolean(root, "isRetryable", defaultValue: false);
        var provider = AiJsonElements.Read<AiProviderMetadata?>(root, "provider", options);
        var diagnostics =
            AiJsonElements.Read<AiGatewayFailureDiagnostics?>(root, "diagnostics", options);
        var reason = AiJsonElements.Read<AiGatewayErrorReason?>(root, "reason", options);

        return reason is { } terminalReason
            ? new AiGatewayError(
                organizationId,
                positionId,
                threadId,
                messageId,
                code,
                message,
                isRetryable,
                provider,
                diagnostics,
                terminalReason)
            : new AiGatewayError(
                organizationId,
                positionId,
                threadId,
                messageId,
                code,
                message,
                isRetryable,
                provider,
                diagnostics);
    }

    public override void Write(
        Utf8JsonWriter writer,
        AiGatewayError value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        AiJsonElements.Write(writer, "organizationId", value.OrganizationId, options);
        AiJsonElements.Write(writer, "positionId", value.PositionId, options);
        AiJsonElements.Write(writer, "threadId", value.ThreadId, options);
        AiJsonElements.Write(writer, "messageId", value.MessageId, options);
        AiJsonElements.Write(writer, "code", value.Code, options);
        writer.WriteString("message", value.Message);
        writer.WriteBoolean("isRetryable", value.IsRetryable);
        AiJsonElements.Write(writer, "provider", value.Provider, options);
        AiJsonElements.Write(writer, "diagnostics", value.Diagnostics, options);
        AiJsonElements.Write(writer, "reason", value.Reason, options);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Field-by-field converter for the response, whose constructor is private and whose success and
/// failure shapes are mutually exclusive. Reading rebuilds the value through the same factories the
/// gateway uses, so an inconsistent payload is rejected by the domain instead of materialized.
/// </summary>
internal sealed class AiGatewayResponseJsonConverter : JsonConverter<AiGatewayResponse>
{
    public override AiGatewayResponse Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var outputConstraintMode =
            AiJsonElements.Read<AiOutputConstraintMode?>(root, "outputConstraintMode", options);
        if (AiJsonElements.Read<AiGatewayError?>(root, "error", options) is { } error)
        {
            return AiGatewayResponse.Failed(error, outputConstraintMode);
        }

        return AiGatewayResponse.Succeeded(
            AiJsonElements.Require<OrganizationId>(root, "organizationId", options),
            AiJsonElements.Require<PositionId>(root, "positionId", options),
            AiJsonElements.Require<ThreadId>(root, "threadId", options),
            AiJsonElements.Require<MessageId>(root, "messageId", options),
            AiJsonElements.Read<string?>(root, "text", options),
            AiJsonElements.Require<AiFinishReason>(root, "finishReason", options),
            AiJsonElements.Read<AiProviderMetadata?>(root, "provider", options),
            AiJsonElements.Read<IReadOnlyList<AiToolCall>>(root, "toolCalls", options),
            AiJsonElements.Read<AiTokenUsage?>(root, "usage", options),
            AiJsonElements.Read<AiCostMetadata?>(root, "cost", options),
            outputConstraintMode,
            AiJsonElements.Read<AiAppliedPricing?>(root, "appliedPricing", options));
    }

    public override void Write(
        Utf8JsonWriter writer,
        AiGatewayResponse value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        AiJsonElements.Write(writer, "organizationId", value.OrganizationId, options);
        AiJsonElements.Write(writer, "positionId", value.PositionId, options);
        AiJsonElements.Write(writer, "threadId", value.ThreadId, options);
        AiJsonElements.Write(writer, "messageId", value.MessageId, options);
        AiJsonElements.Write(writer, "text", value.Text, options);
        AiJsonElements.Write(writer, "finishReason", value.FinishReason, options);
        AiJsonElements.Write(writer, "provider", value.Provider, options);
        AiJsonElements.Write(writer, "toolCalls", value.ToolCalls, options);
        AiJsonElements.Write(writer, "usage", value.Usage, options);
        AiJsonElements.Write(writer, "cost", value.Cost, options);
        AiJsonElements.Write(writer, "appliedPricing", value.AppliedPricing, options);
        AiJsonElements.Write(writer, "error", value.Error, options);
        AiJsonElements.Write(writer, "outputConstraintMode", value.OutputConstraintMode, options);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Tolerant reads for the hand-written AI converters: unknown properties are ignored, a missing
/// optional property yields the default, and a missing required property fails loudly instead of
/// producing a silent default.
/// </summary>
internal static class AiJsonElements
{
    public static void Write<T>(
        Utf8JsonWriter writer,
        string propertyName,
        T value,
        JsonSerializerOptions options)
    {
        writer.WritePropertyName(propertyName);
        JsonSerializer.Serialize(writer, value, options);
    }

    public static T? Read<T>(
        JsonElement root,
        string propertyName,
        JsonSerializerOptions options)
    {
        if (!TryGetProperty(root, propertyName, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return element.Deserialize<T>(options);
    }

    public static T Require<T>(
        JsonElement root,
        string propertyName,
        JsonSerializerOptions options)
    {
        if (!TryGetProperty(root, propertyName, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new JsonException($"Required property '{propertyName}' is missing.");
        }

        var value = element.Deserialize<T>(options);
        if (value is null)
        {
            throw new JsonException($"Property '{propertyName}' deserialized to null.");
        }

        return value;
    }

    public static bool ReadBoolean(JsonElement root, string propertyName, bool defaultValue)
    {
        if (!TryGetProperty(root, propertyName, out var element))
        {
            return defaultValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => defaultValue,
            _ => throw new JsonException($"Property '{propertyName}' must be a boolean."),
        };
    }

    private static bool TryGetProperty(
        JsonElement root,
        string propertyName,
        out JsonElement element)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            element = default;
            return false;
        }

        if (root.TryGetProperty(propertyName, out element))
        {
            return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                element = property.Value;
                return true;
            }
        }

        element = default;
        return false;
    }
}
