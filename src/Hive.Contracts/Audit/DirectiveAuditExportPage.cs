using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Hive.Contracts.Audit;

public sealed record DirectiveAuditExportPage
{
    public DirectiveAuditExportPage(
        string contractName,
        int contractVersion,
        string organizationId,
        Guid threadId,
        Guid directiveId,
        long afterSequence,
        long nextAfterSequence,
        bool isTerminal,
        IReadOnlyList<AuditExportEvent> events,
        AuditExportResult? result = null)
    {
        if (!string.Equals(
                contractName,
                AuditExportContract.Name,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Contract name must be '{AuditExportContract.Name}'.",
                nameof(contractName));
        }

        if (contractVersion != AuditExportContract.Version)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractVersion),
                contractVersion,
                $"Only audit/export contract version {AuditExportContract.Version} is supported.");
        }

        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        if (nextAfterSequence < afterSequence)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextAfterSequence),
                nextAfterSequence,
                "Next cursor cannot precede the requested cursor.");
        }

        ArgumentNullException.ThrowIfNull(events);
        var eventSnapshot = events.ToImmutableArray();
        if (eventSnapshot.Any(item => item is null))
        {
            throw new ArgumentException(
                "Audit export events cannot contain null entries.",
                nameof(events));
        }

        if (eventSnapshot.Length > AuditExportContractLimits.MaxEventsPerPage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(events),
                eventSnapshot.Length,
                $"An audit export page cannot contain more than {AuditExportContractLimits.MaxEventsPerPage} events.");
        }

        var previousSequence = afterSequence;
        foreach (var item in eventSnapshot)
        {
            if (item.Sequence <= previousSequence)
            {
                throw new ArgumentException(
                    "Audit export event sequences must be strictly increasing after the requested cursor.",
                    nameof(events));
            }

            previousSequence = item.Sequence;
        }

        if (nextAfterSequence != previousSequence)
        {
            throw new ArgumentException(
                "Next cursor must equal the final event sequence, or the requested cursor for an empty page.",
                nameof(nextAfterSequence));
        }

        if (!isTerminal && result is not null)
        {
            throw new ArgumentException(
                "A non-terminal audit export page cannot contain a canonical result.",
                nameof(result));
        }

        ContractName = contractName;
        ContractVersion = contractVersion;
        OrganizationId = AuditExportContractGuards.Text(
            organizationId,
            nameof(organizationId));
        ThreadId = AuditExportContractGuards.Identifier(threadId, nameof(threadId));
        DirectiveId = AuditExportContractGuards.Identifier(
            directiveId,
            nameof(directiveId));
        AfterSequence = afterSequence;
        NextAfterSequence = nextAfterSequence;
        IsTerminal = isTerminal;
        Events = eventSnapshot;
        Result = result;
    }

    [JsonPropertyName("contract")]
    public string ContractName { get; }

    [JsonPropertyName("contract_version")]
    public int ContractVersion { get; }

    [JsonPropertyName("organization_id")]
    public string OrganizationId { get; }

    [JsonPropertyName("thread_id")]
    public Guid ThreadId { get; }

    [JsonPropertyName("directive_id")]
    public Guid DirectiveId { get; }

    [JsonPropertyName("after_sequence")]
    public long AfterSequence { get; }

    [JsonPropertyName("next_after_sequence")]
    public long NextAfterSequence { get; }

    [JsonPropertyName("is_terminal")]
    public bool IsTerminal { get; }

    [JsonPropertyName("events")]
    public IReadOnlyList<AuditExportEvent> Events { get; }

    [JsonPropertyName("result")]
    public AuditExportResult? Result { get; }
}
