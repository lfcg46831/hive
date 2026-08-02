using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Domain.Auditing;

/// <summary>
/// Provider-neutral page read by the public audit/export adapter. Sequence numbers belong to
/// the audit store and are exposed only as the v1 cursor; they are not domain authorization.
/// </summary>
public sealed record DirectiveAuditExportPageData
{
    public DirectiveAuditExportPageData(
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId directiveId,
        long afterSequence,
        IEnumerable<DirectiveAuditExportEventData> events,
        bool isTerminal,
        DirectiveAuditExportResultData? result = null)
    {
        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        OrganizationId = organizationId
            ?? throw new ArgumentNullException(nameof(organizationId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        AfterSequence = afterSequence;
        Events = Snapshot(events, afterSequence);
        IsTerminal = isTerminal;
        Result = result;

        if (!isTerminal && result is not null)
        {
            throw new ArgumentException(
                "A non-terminal audit export cannot contain a canonical result.",
                nameof(result));
        }
    }

    public OrganizationId OrganizationId { get; }

    public ThreadId ThreadId { get; }

    public DirectiveId DirectiveId { get; }

    public long AfterSequence { get; }

    public ImmutableArray<DirectiveAuditExportEventData> Events { get; }

    public long NextAfterSequence => Events.IsEmpty ? AfterSequence : Events[^1].Sequence;

    public bool IsTerminal { get; }

    public DirectiveAuditExportResultData? Result { get; }

    private static ImmutableArray<DirectiveAuditExportEventData> Snapshot(
        IEnumerable<DirectiveAuditExportEventData> events,
        long afterSequence)
    {
        ArgumentNullException.ThrowIfNull(events);
        var snapshot = events.ToImmutableArray();
        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException(
                "Audit export events cannot contain null entries.",
                nameof(events));
        }

        var previous = afterSequence;
        foreach (var item in snapshot)
        {
            if (item.Sequence <= previous)
            {
                throw new ArgumentException(
                    "Audit export sequences must be strictly increasing after the cursor.",
                    nameof(events));
            }

            previous = item.Sequence;
        }

        return snapshot;
    }
}

public sealed record DirectiveAuditExportEventData
{
    public DirectiveAuditExportEventData(long sequence, JourneyAuditRecord record)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        Sequence = sequence;
        Record = record ?? throw new ArgumentNullException(nameof(record));
    }

    public long Sequence { get; }

    public JourneyAuditRecord Record { get; }
}

/// <summary>
/// Canonical organizational result captured only by an explicitly enabled audit/export adapter.
/// </summary>
public sealed record DirectiveAuditExportResultData
{
    public DirectiveAuditExportResultData(
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId directiveId,
        PositionId sourcePositionId,
        string messageType,
        int schemaVersion,
        string content,
        DirectiveAuditExportObservationData? acceptedObservation = null)
    {
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        OrganizationId = organizationId
            ?? throw new ArgumentNullException(nameof(organizationId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        SourcePositionId = sourcePositionId
            ?? throw new ArgumentNullException(nameof(sourcePositionId));
        MessageType = RequireText(messageType, nameof(messageType));
        SchemaVersion = schemaVersion;
        Content = RequireText(content, nameof(content));
        AcceptedObservation = acceptedObservation;
    }

    public OrganizationId OrganizationId { get; }

    public ThreadId ThreadId { get; }

    public DirectiveId DirectiveId { get; }

    public PositionId SourcePositionId { get; }

    public string MessageType { get; }

    public int SchemaVersion { get; }

    public string Content { get; }

    /// <summary>
    /// Optional bounded observation retained by the explicitly enabled audit/export adapter when
    /// an accepted organizational result was superseded before emission. This contains only the
    /// opaque observation envelope, never the superseded message itself.
    /// </summary>
    public DirectiveAuditExportObservationData? AcceptedObservation { get; }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        return value;
    }
}

/// <summary>
/// Transient capture handed to an explicitly enabled audit/export adapter. The superseded result
/// is available only so that the adapter can retain its bounded observation; storage adapters must
/// never persist the superseded organizational message itself.
/// </summary>
public sealed record DirectiveAuditExportResultCaptureData
{
    public DirectiveAuditExportResultCaptureData(
        DirectiveAuditExportResultData result,
        DirectiveAuditExportMessageData? supersededResult = null)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        if (result.AcceptedObservation is not null)
        {
            throw new ArgumentException(
                "A new audit/export capture cannot carry a precomputed accepted observation.",
                nameof(result));
        }

        SupersededResult = supersededResult;
    }

    public DirectiveAuditExportResultData Result { get; }

    public DirectiveAuditExportMessageData? SupersededResult { get; }
}

/// <summary>
/// Canonical organizational message carried transiently across the audit/export adapter seam.
/// </summary>
public sealed record DirectiveAuditExportMessageData
{
    public DirectiveAuditExportMessageData(
        string messageType,
        int schemaVersion,
        string content)
    {
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        MessageType = RequireText(messageType, nameof(messageType));
        SchemaVersion = schemaVersion;
        Content = RequireText(content, nameof(content));
    }

    public string MessageType { get; }

    public int SchemaVersion { get; }

    public string Content { get; }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        return value;
    }
}

/// <summary>
/// Bounded, content-free observation projected from an accepted result that was superseded.
/// </summary>
public sealed record DirectiveAuditExportObservationData
{
    public const int CurrentContractVersion = 1;

    public DirectiveAuditExportObservationData(int contractVersion, string content)
    {
        if (contractVersion != CurrentContractVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractVersion),
                contractVersion,
                $"Only accepted-observation contract version {CurrentContractVersion} is supported.");
        }

        ContractVersion = contractVersion;
        Content = string.IsNullOrWhiteSpace(content)
            ? throw new ArgumentException(
                "Accepted observation content cannot be empty or whitespace.",
                nameof(content))
            : content;
    }

    public int ContractVersion { get; }

    public string Content { get; }
}

public interface IDirectiveAuditExportReader
{
    ValueTask<DirectiveAuditExportPageData> ReadAsync(
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId directiveId,
        long afterSequence,
        int pageSize,
        CancellationToken cancellationToken = default);
}

public interface IDirectiveAuditExportResultSink
{
    ValueTask StoreAsync(
        DirectiveAuditExportResultCaptureData capture,
        CancellationToken cancellationToken = default);
}

public sealed class NoopDirectiveAuditExportStore :
    IDirectiveAuditExportReader,
    IDirectiveAuditExportResultSink
{
    public static NoopDirectiveAuditExportStore Instance { get; } = new();

    private NoopDirectiveAuditExportStore()
    {
    }

    public ValueTask<DirectiveAuditExportPageData> ReadAsync(
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId directiveId,
        long afterSequence,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new DirectiveAuditExportPageData(
            organizationId,
            threadId,
            directiveId,
            afterSequence,
            [],
            isTerminal: false));
    }

    public ValueTask StoreAsync(
        DirectiveAuditExportResultCaptureData capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
