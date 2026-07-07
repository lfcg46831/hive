using Hive.Domain.Messaging;

namespace Hive.Api.Directives;

public sealed record DirectiveSubmissionEndpointRefRequest(
    string? Kind,
    string? PositionId);

public sealed record SubmitDirectiveRequest(
    string? MessageId,
    DirectiveSubmissionEndpointRefRequest? From,
    DirectiveSubmissionEndpointRefRequest? To,
    string? ThreadId,
    string? Priority,
    int? SchemaVersion,
    DateTimeOffset? SentAt,
    DateTimeOffset? Deadline,
    string? DirectiveId,
    string? ParentDirectiveId,
    string? Objective,
    string? Context);

public sealed record SubmitDirectiveResponse(
    string Status,
    string MessageId,
    string OrganizationId,
    string FromPositionId,
    string ToPositionId,
    string ThreadId,
    string DirectiveId);

public sealed record DirectiveSubmissionErrorResponse(
    string Code,
    string Path,
    string Reason);

public interface IDirectiveSubmissionSink
{
    ValueTask<DirectiveSubmissionResult> SubmitAsync(
        Directive directive,
        CancellationToken cancellationToken);
}

public sealed record DirectiveSubmissionResult(
    Directive Directive,
    RoutingRejection? Rejection = null)
{
    public bool IsAccepted => Rejection is null;

    public static DirectiveSubmissionResult Accepted(Directive directive)
    {
        ArgumentNullException.ThrowIfNull(directive);
        return new DirectiveSubmissionResult(directive);
    }

    public static DirectiveSubmissionResult Rejected(
        Directive directive,
        RoutingRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(directive);
        ArgumentNullException.ThrowIfNull(rejection);

        return new DirectiveSubmissionResult(directive, rejection);
    }
}
