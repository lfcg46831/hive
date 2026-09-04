using System.Globalization;

namespace Hive.Domain.Ai;

/// <summary>
/// Distinguishes the two cost/audit events emitted for one gateway call (US-F1-05-T08):
/// one <see cref="Attempt"/> event per attempt, which is the idempotent debit unit, and
/// exactly one <see cref="Journey"/> event summarising the terminal response.
/// </summary>
public enum AiGatewayCostAuditScope
{
    Attempt = 1,
    Journey = 2,
}

public static class AiGatewayCostAuditScopeContract
{
    private static readonly AiProtocolEnumWireContract<AiGatewayCostAuditScope> Contract = new(
        (AiGatewayCostAuditScope.Attempt, "attempt"),
        (AiGatewayCostAuditScope.Journey, "journey"));

    public static AiGatewayCostAuditScope RequireDefined(
        AiGatewayCostAuditScope value,
        string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(AiGatewayCostAuditScope value) =>
        Contract.ToWireValue(value);

    public static AiGatewayCostAuditScope ParseWireValue(string value) =>
        Contract.ParseWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out AiGatewayCostAuditScope scope) =>
        Contract.TryParseWireValue(value, out scope);
}

/// <summary>
/// Identity and local measurement of a single gateway attempt (US-F1-05-T08). The
/// identity is derived exclusively from the position of the attempt in the journey,
/// never from a clock or a GUID, so the same journey replays to the same identity.
/// </summary>
public sealed record AiGatewayCostAuditAttempt
{
    public AiGatewayCostAuditAttempt(
        int candidateIndex,
        int attempt,
        bool reachedProvider,
        TimeSpan? queueDuration = null)
    {
        if (candidateIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidateIndex),
                candidateIndex,
                "AI gateway attempt candidate index cannot be negative.");
        }

        if (attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt),
                attempt,
                "AI gateway attempt number must be greater than zero.");
        }

        if (queueDuration is { } queued && queued < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queueDuration),
                queueDuration,
                "AI gateway attempt queue duration cannot be negative.");
        }

        CandidateIndex = candidateIndex;
        Attempt = attempt;
        ReachedProvider = reachedProvider;
        QueueDuration = queueDuration;
        AttemptId = FormatAttemptId(candidateIndex, attempt);
    }

    /// <summary>
    /// Deterministic identity of the attempt inside its gateway call. Combined with the
    /// organization, thread, message, operation and iteration it is the replay-safe key
    /// that the durable cost ledger debits exactly once.
    /// </summary>
    public string AttemptId { get; }

    /// <summary>Zero-based position in the declared chain; zero is the primary request.</summary>
    public int CandidateIndex { get; }

    /// <summary>One-based attempt number inside the candidate.</summary>
    public int Attempt { get; }

    /// <summary>True only when the attempt was admitted and handed to the adapter.</summary>
    public bool ReachedProvider { get; }

    /// <summary>Wait measured by the admission lease of US-F1-05-T03, when one was granted.</summary>
    public TimeSpan? QueueDuration { get; }

    public static string FormatAttemptId(int candidateIndex, int attempt) =>
        candidateIndex.ToString(CultureInfo.InvariantCulture) +
        "-" +
        attempt.ToString(CultureInfo.InvariantCulture);
}
