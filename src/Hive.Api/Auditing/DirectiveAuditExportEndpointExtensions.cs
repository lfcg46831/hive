using System.Globalization;
using System.Text;
using Hive.Contracts.Audit;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Hive.Api.Auditing;

public static class DirectiveAuditExportEndpointExtensions
{
    public const string Route =
        "/api/v1/organizations/{organizationId}/threads/{threadId}/directives/{directiveId}/audit-export";

    public static IEndpointRouteBuilder MapHiveDirectiveAuditExportApi(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(Route, ReadAsync)
            .WithName("GetDirectiveAuditExportV1");
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(
        string organizationId,
        string threadId,
        string directiveId,
        long? after_sequence,
        IDirectiveAuditExportReader reader,
        CancellationToken cancellationToken)
    {
        if (!TryParseScope(
                organizationId,
                threadId,
                directiveId,
                after_sequence,
                out var organization,
                out var thread,
                out var directive,
                out var cursor,
                out var problem))
        {
            return problem!;
        }

        var data = await reader.ReadAsync(
                organization!,
                thread!,
                directive!,
                cursor,
                AuditExportContractLimits.MaxEventsPerPage,
                cancellationToken)
            .ConfigureAwait(false);
        var events = data.Events
            .Select(item => Map(item.Sequence, item.Record))
            .ToArray();
        var result = MapResult(data.Result);

        return TypedResults.Ok(new DirectiveAuditExportPage(
            AuditExportContract.Name,
            AuditExportContract.Version,
            data.OrganizationId.Value,
            data.ThreadId.Value,
            data.DirectiveId.Value,
            data.AfterSequence,
            data.NextAfterSequence,
            data.IsTerminal,
            events,
            result));
    }

    private static AuditExportEvent Map(long sequence, JourneyAuditRecord record) =>
        new(
            sequence,
            record.AuditEventId,
            record.OccurredAtUtc.ToUniversalTime(),
            record.PersistedAtUtc.ToUniversalTime(),
            record.Stage.ToString(),
            record.Outcome.ToString(),
            record.MessageId.Value,
            record.PositionId?.Value,
            record.ReasonCode,
            record.MessageType,
            MapProvider(record),
            MapUsage(record),
            MapCost(record),
            record.Latency is { } latency
                ? Convert.ToInt64(latency.TotalMilliseconds, CultureInfo.InvariantCulture)
                : null,
            BoundAttributes(record.Payload));

    private static AuditExportProvider? MapProvider(JourneyAuditRecord record) =>
        record.Provider is { } provider
            ? new AuditExportProvider(provider.ProviderId, provider.ModelId)
            : null;

    private static AuditExportUsage? MapUsage(JourneyAuditRecord record) =>
        record.Usage is
        {
            InputTokens: { } input,
            OutputTokens: { } output,
            TotalTokens: { } total,
        } usage
            ? new AuditExportUsage(input, output, total, usage.IsEstimated)
            : null;

    private static AuditExportCost? MapCost(JourneyAuditRecord record)
    {
        if (record.Cost is not { } cost)
        {
            return null;
        }

        var payload = record.Payload;
        var pricingTokenUnit = 0;
        var inputPricePerTokenUnit = 0m;
        var outputPricePerTokenUnit = 0m;
        var hasCompletePricing =
            payload.TryGetValue("pricingVersion", out var pricingVersion) &&
            TryPositiveInt(payload, "pricingTokenUnit", out pricingTokenUnit) &&
            TryNonNegativeDecimal(
                payload,
                "inputPricePerTokenUnit",
                out inputPricePerTokenUnit) &&
            TryNonNegativeDecimal(
                payload,
                "outputPricePerTokenUnit",
                out outputPricePerTokenUnit);

        return hasCompletePricing
            ? new AuditExportCost(
                cost.Amount,
                cost.Currency.ToUpperInvariant(),
                cost.IsEstimated,
                pricingVersion,
                pricingTokenUnit,
                inputPricePerTokenUnit,
                outputPricePerTokenUnit)
            : new AuditExportCost(
                cost.Amount,
                cost.Currency.ToUpperInvariant(),
                cost.IsEstimated);
    }

    private static AuditExportResult? MapResult(
        DirectiveAuditExportResultData? result)
    {
        if (result is null ||
            Encoding.UTF8.GetByteCount(result.Content) >
            AuditExportContractLimits.MaxResultContentBytes)
        {
            return null;
        }

        return AuditExportResult.Create(
            result.MessageType,
            result.SchemaVersion,
            result.Content);
    }

    private static IReadOnlyDictionary<string, string> BoundAttributes(
        IReadOnlyDictionary<string, string> attributes)
    {
        var bounded = new Dictionary<string, string>(StringComparer.Ordinal);
        var payloadBytes = 0;
        foreach (var (rawKey, rawValue) in attributes
            .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (IsForbiddenAttribute(rawKey))
            {
                continue;
            }

            if (bounded.Count == AuditExportContractLimits.MaxAttributesPerEvent)
            {
                break;
            }

            var key = rawKey.Length <= AuditExportContractLimits.MaxAttributeKeyLength
                ? rawKey
                : rawKey[..AuditExportContractLimits.MaxAttributeKeyLength];
            var value = rawValue.Length <= AuditExportContractLimits.MaxAttributeValueLength
                ? rawValue
                : rawValue[..AuditExportContractLimits.MaxAttributeValueLength];
            var nextBytes = payloadBytes +
                Encoding.UTF8.GetByteCount(key) +
                Encoding.UTF8.GetByteCount(value);
            if (nextBytes > AuditExportContractLimits.MaxAttributePayloadBytes ||
                bounded.ContainsKey(key))
            {
                continue;
            }

            bounded.Add(key, value);
            payloadBytes = nextBytes;
        }

        return bounded;
    }

    private static bool IsForbiddenAttribute(string key)
    {
        var normalized = new string(
            key.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        string[] forbiddenFragments =
        [
            "prompt",
            "input",
            "memory",
            "raw",
            "rejectedoutput",
            "reasoning",
            "chainofthought",
            "trace",
            "toolargument",
            "toolpayload",
            "toolresult",
            "authorizationheader",
            "apikey",
            "connectionstring",
            "secret",
        ];

        return forbiddenFragments.Any(normalized.Contains);
    }

    private static bool TryParseScope(
        string organizationId,
        string threadId,
        string directiveId,
        long? afterSequence,
        out OrganizationId? organization,
        out ThreadId? thread,
        out DirectiveId? directive,
        out long cursor,
        out IResult? problem)
    {
        organization = null;
        thread = null;
        directive = null;
        cursor = afterSequence ?? 0;
        problem = null;

        try
        {
            organization = OrganizationId.From(organizationId);
            if (!Guid.TryParse(threadId, out var parsedThread) ||
                !Guid.TryParse(directiveId, out var parsedDirective))
            {
                throw new ArgumentException(
                    "Thread and directive identifiers must be non-empty GUIDs.");
            }

            thread = ThreadId.From(parsedThread);
            directive = DirectiveId.From(parsedDirective);
            if (cursor < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(afterSequence),
                    "The after_sequence cursor cannot be negative.");
            }

            return true;
        }
        catch (ArgumentException exception)
        {
            problem = TypedResults.Problem(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid directive audit/export request",
                Detail = exception.Message,
            });
            return false;
        }
    }

    private static bool TryPositiveInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        out int result)
    {
        result = 0;
        return values.TryGetValue(key, out var value) &&
            int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out result) &&
            result > 0;
    }

    private static bool TryNonNegativeDecimal(
        IReadOnlyDictionary<string, string> values,
        string key,
        out decimal result)
    {
        result = 0;
        return values.TryGetValue(key, out var value) &&
            decimal.TryParse(
                value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out result) &&
            result >= 0;
    }
}
