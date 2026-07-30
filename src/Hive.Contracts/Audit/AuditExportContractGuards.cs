using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Hive.Contracts.Audit;

internal static class AuditExportContractGuards
{
    private const int MaxTokenLength = 256;

    public static string Text(
        string value,
        string parameterName,
        int maxLength = MaxTokenLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Value cannot be empty or whitespace.",
                parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Value cannot contain leading or trailing whitespace.",
                parameterName);
        }

        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value.Length,
                $"Value cannot exceed {maxLength} characters.");
        }

        return value;
    }

    public static string? OptionalText(
        string? value,
        string parameterName,
        int maxLength = MaxTokenLength) =>
        value is null ? null : Text(value, parameterName, maxLength);

    public static Guid Identifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Identifier cannot be empty.",
                parameterName);
        }

        return value;
    }

    public static DateTimeOffset UtcTimestamp(
        DateTimeOffset value,
        string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentException(
                "Timestamp must be specified.",
                parameterName);
        }

        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Timestamp must use the UTC offset.",
                parameterName);
        }

        return value;
    }

    public static ImmutableSortedDictionary<string, string> Attributes(
        IReadOnlyDictionary<string, string>? attributes,
        string parameterName)
    {
        if (attributes is null || attributes.Count == 0)
        {
            return ImmutableSortedDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal);
        }

        if (attributes.Count > AuditExportContractLimits.MaxAttributesPerEvent)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                attributes.Count,
                $"An event cannot contain more than {AuditExportContractLimits.MaxAttributesPerEvent} attributes.");
        }

        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(
            StringComparer.Ordinal);
        var payloadBytes = 0;
        foreach (var (key, value) in attributes)
        {
            var guardedKey = Text(
                key,
                parameterName,
                AuditExportContractLimits.MaxAttributeKeyLength);
            var guardedValue = value
                ?? throw new ArgumentException(
                    "Attribute values cannot be null.",
                    parameterName);
            if (guardedValue.Length > AuditExportContractLimits.MaxAttributeValueLength)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    guardedValue.Length,
                    $"Attribute values cannot exceed {AuditExportContractLimits.MaxAttributeValueLength} characters.");
            }

            payloadBytes += Encoding.UTF8.GetByteCount(guardedKey);
            payloadBytes += Encoding.UTF8.GetByteCount(guardedValue);
            if (payloadBytes > AuditExportContractLimits.MaxAttributePayloadBytes)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    payloadBytes,
                    $"Event attributes cannot exceed {AuditExportContractLimits.MaxAttributePayloadBytes} UTF-8 bytes.");
            }

            builder.Add(guardedKey, guardedValue);
        }

        return builder.ToImmutable();
    }

    public static string CanonicalJsonContent(
        string content,
        int contentLengthBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(content, parameterName);
        var actualLength = Encoding.UTF8.GetByteCount(content);
        if (actualLength == 0)
        {
            throw new ArgumentException(
                "Result content cannot be empty.",
                parameterName);
        }

        if (actualLength != contentLengthBytes)
        {
            throw new ArgumentException(
                "Result content length does not match its declared UTF-8 byte length.",
                parameterName);
        }

        if (actualLength > AuditExportContractLimits.MaxResultContentBytes)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                actualLength,
                $"Result content cannot exceed {AuditExportContractLimits.MaxResultContentBytes} UTF-8 bytes.");
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "Result content must be a JSON object.",
                    parameterName);
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Result content must be valid JSON.",
                parameterName,
                exception);
        }

        return content;
    }
}
