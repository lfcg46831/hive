using System.Collections.Immutable;
using System.Text;
using Hive.Domain.Identity;

namespace Hive.Domain.Outcomes;

public enum OutcomeVerifierClassification
{
    ContinueWork = 1,
    ReportProgress = 2,
    ReportDone = 3,
    Escalation = 4,
    Directive = 5,
    ApprovalRequired = 6,
    Undetermined = 7,
}

public static class OutcomeVerifierClassificationContract
{
    private static readonly OutcomeEnumWireContract<OutcomeVerifierClassification> Contract = new(
        (OutcomeVerifierClassification.ContinueWork, "ContinueWork"),
        (OutcomeVerifierClassification.ReportProgress, "Report.Progress"),
        (OutcomeVerifierClassification.ReportDone, "Report.Done"),
        (OutcomeVerifierClassification.Escalation, "Escalation"),
        (OutcomeVerifierClassification.Directive, "Directive"),
        (OutcomeVerifierClassification.ApprovalRequired, "ApprovalRequired"),
        (OutcomeVerifierClassification.Undetermined, "Undetermined"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeVerifierClassification RequireDefined(
        OutcomeVerifierClassification value,
        string parameterName) => Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeVerifierClassification value) =>
        Contract.ToWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out OutcomeVerifierClassification result) => Contract.TryParseWireValue(value, out result);
}

public enum OutcomeVerifierResultStatus
{
    Classified = 1,
    Unavailable = 2,
    TimedOut = 3,
    InvalidOutput = 4,
}

public static class OutcomeVerifierResultStatusContract
{
    private static readonly OutcomeEnumWireContract<OutcomeVerifierResultStatus> Contract = new(
        (OutcomeVerifierResultStatus.Classified, "Classified"),
        (OutcomeVerifierResultStatus.Unavailable, "Unavailable"),
        (OutcomeVerifierResultStatus.TimedOut, "TimedOut"),
        (OutcomeVerifierResultStatus.InvalidOutput, "InvalidOutput"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeVerifierResultStatus RequireDefined(
        OutcomeVerifierResultStatus value,
        string parameterName) => Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeVerifierResultStatus value) =>
        Contract.ToWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out OutcomeVerifierResultStatus result) => Contract.TryParseWireValue(value, out result);
}

public sealed record OutcomeVerificationContextEntry
{
    public OutcomeVerificationContextEntry(string reference, string value)
    {
        Reference = OutcomeContractGuards.RequireReference(reference, nameof(reference));
        Value = OutcomeContractGuards.RequireText(value, nameof(value));
    }

    public string Reference { get; }

    public string Value { get; }
}

/// <summary>
/// Bounded semantic context selected for the verifier. The contract deliberately exposes no
/// tools, conversation history, memory handles, provider settings, or write-capable services.
/// </summary>
public sealed record OutcomeVerificationContext
{
    public const int MaximumEntries = 8;
    public const int MaximumUtf8Bytes = 4096;

    public OutcomeVerificationContext(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId messageId,
        DirectiveId directiveId,
        TimeSpan timeout,
        IEnumerable<OutcomeVerificationContextEntry>? entries = null,
        int? executionLimitsVersion = null,
        TimeSpan? executionBudget = null,
        TimeSpan? perCallTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(directiveId);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Outcome verifier timeout must be greater than zero.");
        }

        if (executionLimitsVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(executionLimitsVersion),
                executionLimitsVersion,
                "Execution limits version cannot be negative.");
        }

        if (executionBudget is { } budget && budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(executionBudget),
                executionBudget,
                "Execution budget must be greater than zero.");
        }

        if (perCallTimeout is { } perCall && perCall <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(perCallTimeout),
                perCallTimeout,
                "Per-call timeout must be greater than zero.");
        }

        var snapshot = entries is null
            ? ImmutableArray<OutcomeVerificationContextEntry>.Empty
            : entries.ToImmutableArray();
        if (snapshot.Any(entry => entry is null))
        {
            throw new ArgumentException(
                "Outcome verification context cannot contain null entries.",
                nameof(entries));
        }

        if (snapshot.Length > MaximumEntries)
        {
            throw new ArgumentException(
                $"Outcome verification context cannot contain more than {MaximumEntries} entries.",
                nameof(entries));
        }

        if (snapshot.Select(entry => entry.Reference)
            .Distinct(StringComparer.Ordinal)
            .Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Outcome verification context references must be unique.",
                nameof(entries));
        }

        var ordered = snapshot
            .OrderBy(entry => entry.Reference, StringComparer.Ordinal)
            .ToImmutableArray();
        var utf8Bytes = ordered.Sum(entry =>
            Encoding.UTF8.GetByteCount(entry.Reference) +
            Encoding.UTF8.GetByteCount(entry.Value));
        if (utf8Bytes > MaximumUtf8Bytes)
        {
            throw new ArgumentException(
                $"Outcome verification context cannot exceed {MaximumUtf8Bytes} UTF-8 bytes.",
                nameof(entries));
        }

        OrganizationId = organizationId;
        PositionId = positionId;
        ThreadId = threadId;
        MessageId = messageId;
        DirectiveId = directiveId;
        Timeout = timeout;
        ExecutionLimitsVersion = executionLimitsVersion;
        ExecutionBudget = executionBudget;
        PerCallTimeout = perCallTimeout;
        Entries = ordered;
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public ThreadId ThreadId { get; }

    public MessageId MessageId { get; }

    /// <summary>
    /// Stable directive identity used to correlate the verifier call with the same minimized
    /// audit journey as the primary inference.
    /// </summary>
    public DirectiveId DirectiveId { get; }

    /// <summary>
    /// Maximum verifier call duration selected by the caller; runtime composition must cap it at
    /// the remaining directive deadline.
    /// </summary>
    public TimeSpan Timeout { get; }

    public int? ExecutionLimitsVersion { get; }

    public TimeSpan? ExecutionBudget { get; }

    public TimeSpan? PerCallTimeout { get; }

    public ImmutableArray<OutcomeVerificationContextEntry> Entries { get; }
}

public sealed record OutcomeVerificationArtifactEntry
{
    public OutcomeVerificationArtifactEntry(string reference, string value)
    {
        Reference = OutcomeContractGuards.RequireReference(reference, nameof(reference));
        Value = OutcomeContractGuards.RequireText(value, nameof(value));
    }

    public string Reference { get; }

    public string Value { get; }
}

/// <summary>
/// Bounded semantic projection of the already materialized organizational message. This is not
/// provider output and deliberately excludes tools, history, memory, attachments, reasoning,
/// evaluation envelopes, and rejected values.
/// </summary>
public sealed record OutcomeVerificationArtifact
{
    public const int MaximumEntries = 16;
    public const int MaximumUtf8Bytes = 16 * 1024;

    public OutcomeVerificationArtifact(
        OutcomeKind kind,
        IEnumerable<OutcomeVerificationArtifactEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Kind = OutcomeKindContract.RequireDefined(kind, nameof(kind));
        if (kind is OutcomeKind.ContinueWork or OutcomeKind.Undetermined)
        {
            throw new ArgumentException(
                "A verification artifact must represent a materialized organizational message.",
                nameof(kind));
        }

        var snapshot = entries.ToImmutableArray();
        if (snapshot.IsEmpty)
        {
            throw new ArgumentException(
                "A verification artifact must contain at least one semantic field.",
                nameof(entries));
        }

        if (snapshot.Any(entry => entry is null))
        {
            throw new ArgumentException(
                "A verification artifact cannot contain null entries.",
                nameof(entries));
        }

        if (snapshot.Length > MaximumEntries)
        {
            throw new ArgumentException(
                $"A verification artifact cannot contain more than {MaximumEntries} entries.",
                nameof(entries));
        }

        if (snapshot.Select(entry => entry.Reference)
            .Distinct(StringComparer.Ordinal)
            .Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Verification artifact references must be unique.",
                nameof(entries));
        }

        var ordered = snapshot
            .OrderBy(entry => entry.Reference, StringComparer.Ordinal)
            .ToImmutableArray();
        var utf8Bytes = Encoding.UTF8.GetByteCount(OutcomeKindContract.ToWireValue(kind)) +
            ordered.Sum(entry =>
                Encoding.UTF8.GetByteCount(entry.Reference) +
                Encoding.UTF8.GetByteCount(entry.Value));
        if (utf8Bytes > MaximumUtf8Bytes)
        {
            throw new ArgumentException(
                $"A verification artifact cannot exceed {MaximumUtf8Bytes} UTF-8 bytes.",
                nameof(entries));
        }

        Entries = ordered;
        Utf8Bytes = utf8Bytes;
    }

    public OutcomeKind Kind { get; }

    public ImmutableArray<OutcomeVerificationArtifactEntry> Entries { get; }

    public int Utf8Bytes { get; }
}

public sealed record OutcomeVerificationRequest
{
    public OutcomeVerificationRequest(
        OutcomeVerificationContext context,
        ExecutionFacts facts,
        DirectiveExecutionContract directive,
        OutcomeProposal proposal,
        OutcomePolicySnapshot policy,
        OutcomeVerificationArtifact? artifact = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Facts = facts ?? throw new ArgumentNullException(nameof(facts));
        Directive = directive ?? throw new ArgumentNullException(nameof(directive));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (artifact is not null && !IsCompatible(proposal.ProposedIntent, artifact.Kind))
        {
            throw new ArgumentException(
                "The verification artifact contradicts the proposed organizational outcome.",
                nameof(artifact));
        }

        Artifact = artifact;
    }

    public int ContractVersion => OrganizationalOutcomeContractVersions.OutcomeVerification;

    public OutcomeVerificationContext Context { get; }

    public ExecutionFacts Facts { get; }

    public DirectiveExecutionContract Directive { get; }

    public OutcomeProposal Proposal { get; }

    public OutcomePolicySnapshot Policy { get; }

    public OutcomeVerificationArtifact? Artifact { get; }

    private static bool IsCompatible(OutcomeProposedIntent intent, OutcomeKind kind) =>
        intent switch
        {
            OutcomeProposedIntent.ContinueWork or OutcomeProposedIntent.ReportProgress =>
                kind == OutcomeKind.ReportProgress,
            OutcomeProposedIntent.ReportDone => kind == OutcomeKind.ReportDone,
            OutcomeProposedIntent.Escalation => kind == OutcomeKind.Escalation,
            OutcomeProposedIntent.Directive => kind == OutcomeKind.Directive,
            OutcomeProposedIntent.ApprovalRequired => kind == OutcomeKind.ApprovalRequired,
            _ => false,
        };
}

public sealed record OutcomeVerifierResult
{
    private OutcomeVerifierResult(
        OutcomeVerifierResultStatus status,
        OutcomeVerifierClassification? classification)
    {
        Status = OutcomeContractGuards.RequireDefined(status, nameof(status));
        if ((status == OutcomeVerifierResultStatus.Classified) != classification.HasValue)
        {
            throw new ArgumentException(
                "Only a classified verifier result can carry a classification.",
                nameof(classification));
        }

        Classification = classification is null
            ? null
            : OutcomeVerifierClassificationContract.RequireDefined(
                classification.Value,
                nameof(classification));
    }

    public OutcomeVerifierResultStatus Status { get; }

    public OutcomeVerifierClassification? Classification { get; }

    public static OutcomeVerifierResult Classified(OutcomeVerifierClassification classification) =>
        new(OutcomeVerifierResultStatus.Classified, classification);

    public static OutcomeVerifierResult Unavailable() =>
        new(OutcomeVerifierResultStatus.Unavailable, classification: null);

    public static OutcomeVerifierResult TimedOut() =>
        new(OutcomeVerifierResultStatus.TimedOut, classification: null);

    public static OutcomeVerifierResult InvalidOutput() =>
        new(OutcomeVerifierResultStatus.InvalidOutput, classification: null);
}

public interface IOutcomeVerifier
{
    Task<OutcomeVerifierResult> VerifyAsync(
        OutcomeVerificationRequest request,
        CancellationToken cancellationToken = default);
}
