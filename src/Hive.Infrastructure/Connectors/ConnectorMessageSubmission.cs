using Hive.Domain.Messaging;

namespace Hive.Infrastructure.Connectors;

/// <summary>
/// Host seam used by in-process connector plugins to submit an already mapped canonical message
/// to the organizational runtime and wait for its durable acceptance acknowledgement.
/// </summary>
public interface IConnectorMessageSubmissionSink
{
    ValueTask<ConnectorMessageSubmissionResult> SubmitAsync(
        OrgMessage message,
        CancellationToken cancellationToken = default);
}

public enum ConnectorMessageSubmissionDecision
{
    Accepted = 1,
    AlreadyAccepted = 2,
}

public sealed record ConnectorMessageSubmissionResult
{
    public ConnectorMessageSubmissionResult(ConnectorMessageSubmissionDecision decision)
    {
        Decision = decision is ConnectorMessageSubmissionDecision.Accepted
            or ConnectorMessageSubmissionDecision.AlreadyAccepted
                ? decision
                : throw new ArgumentOutOfRangeException(
                    nameof(decision),
                    decision,
                    "Connector message submission decision is undefined.");
    }

    public ConnectorMessageSubmissionDecision Decision { get; }

    public static ConnectorMessageSubmissionResult Accepted() =>
        new(ConnectorMessageSubmissionDecision.Accepted);

    public static ConnectorMessageSubmissionResult AlreadyAccepted() =>
        new(ConnectorMessageSubmissionDecision.AlreadyAccepted);
}
