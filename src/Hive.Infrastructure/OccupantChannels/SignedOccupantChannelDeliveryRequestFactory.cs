using System.Globalization;
using Hive.Domain.Messaging;
using Hive.Domain.OccupantChannels;

namespace Hive.Infrastructure.OccupantChannels;

/// <summary>
/// Materializes the channel-neutral request from a durable message using the signed correlation
/// service. Transport-specific wrapping remains in the SMTP adapter.
/// </summary>
internal sealed class SignedOccupantChannelDeliveryRequestFactory
    : IOccupantChannelDeliveryRequestFactory
{
    private readonly IOccupantChannelCorrelationTokenService _correlationTokens;

    public SignedOccupantChannelDeliveryRequestFactory(
        IOccupantChannelCorrelationTokenService correlationTokens) =>
        _correlationTokens = correlationTokens ??
            throw new ArgumentNullException(nameof(correlationTokens));

    public OccupantChannelDeliveryRequest Create(OccupantChannelDeliveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var binding = context.OccupantChannelBindingId ??
            throw new InvalidOperationException(
                "An active opaque binding is required to build an occupant-channel request.");
        var message = context.Message;
        var requestId = message is ApprovalRequest ? message.Id : null;
        var token = _correlationTokens.Issue(new OccupantChannelCorrelationTokenRequest(
            context.OrganizationId,
            context.PositionId,
            message.Id,
            message.Thread,
            requestId));

        return new OccupantChannelDeliveryRequest(
            context.OrganizationId,
            context.PositionId,
            context.OccupantId,
            context.UserId,
            binding,
            message.Id,
            message.Thread,
            Render(message),
            token);
    }

    private static string Render(OrgMessage message)
    {
        var lines = new List<string>
        {
            $"Type: {message.GetType().Name}",
            $"From: {Endpoint(message.From)}",
            $"Priority: {message.Priority}",
            $"Sent at (UTC): {message.SentAt.ToUniversalTime():O}",
        };

        if (message.Deadline is { } deadline)
        {
            lines.Add($"Deadline (UTC): {deadline.ToUniversalTime():O}");
        }

        lines.Add(string.Empty);
        lines.AddRange(Content(message));
        return string.Join('\n', lines);
    }

    private static IEnumerable<string> Content(OrgMessage message) => message switch
    {
        Directive directive =>
        [
            "Objective:",
            directive.Objective,
            string.Empty,
            "Context:",
            directive.Context,
        ],
        Report report =>
        [
            $"Report kind: {report.Kind}",
            $"About directive: {report.AboutDirectiveId.Value:D}",
            string.Empty,
            report.Body,
        ],
        Escalation escalation =>
        [
            "Issue:",
            escalation.Issue,
            string.Empty,
            "Context:",
            escalation.Context,
            string.Empty,
            "Options considered:",
            .. escalation.OptionsConsidered.Select(option => $"- {option}"),
        ],
        Memo memo => [memo.Body],
        PeerRequest request => ["Request:", request.Ask],
        PeerResponse response =>
        [
            $"In reply to: {response.InReplyTo.Value:D}",
            string.Empty,
            response.Body,
        ],
        ApprovalRequest request =>
        [
            $"Approval request: {request.Id.Value:D}",
            "Action:",
            request.Action,
            string.Empty,
            "Justification:",
            request.Justification,
        ],
        ApprovalDecision decision =>
        [
            $"Approval request: {decision.RequestId.Value:D}",
            $"Decision: {(decision.Approved ? "approved" : "rejected")}",
            $"Reason: {decision.Reason ?? "(none)"}",
        ],
        _ => throw new InvalidOperationException(
            $"Organizational message '{message.GetType().Name}' cannot be rendered for an occupant channel."),
    };

    private static string Endpoint(EndpointRef endpoint) => endpoint switch
    {
        PositionEndpointRef position => $"position:{position.PositionId.Value}",
        OrganizationOwnerEndpointRef => "organization-owner",
        SystemEndpointRef system => $"system:{system.Kind.ToString().ToLower(CultureInfo.InvariantCulture)}",
        _ => throw new InvalidOperationException(
            $"Endpoint '{endpoint.GetType().Name}' cannot be rendered for an occupant channel."),
    };
}
