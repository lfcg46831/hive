using Hive.Domain.OccupantChannels;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed class SmtpOccupantEmailRenderer
{
    public SmtpOutboundMessage Render(
        OccupantChannelDeliveryRequest request,
        string normalizedRecipient,
        SmtpOccupantChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRecipient);
        ArgumentNullException.ThrowIfNull(options);

        var fromAddress = options.FromAddress ??
            throw new InvalidOperationException("SMTP sender address was not validated.");

        var subject = $"{options.SubjectPrefix} Position notification {request.MessageId.Value:N}";
        var body = string.Join(
            '\n',
            "HIVE organizational notification",
            string.Empty,
            "The position inbox remains the source of truth for this work.",
            $"Organization: {request.OrganizationId.Value}",
            $"Position: {request.PositionId.Value}",
            $"Thread: {request.ThreadId.Value:D}",
            string.Empty,
            "--- organizational message ---",
            request.RenderedMessage,
            "--- end organizational message ---",
            string.Empty,
            "Reply in plain text above the token line and leave that line unchanged.",
            $"HIVE-Occupant-Correlation: {request.CorrelationToken.Value}");

        var senderDomain = fromAddress[(fromAddress.LastIndexOf('@') + 1)..];
        var transportMessageId = $"hive-{request.MessageId.Value:N}@{senderDomain}";

        return new SmtpOutboundMessage(
            normalizedRecipient,
            subject,
            body,
            transportMessageId,
            request.MessageId.Value.ToString("D"));
    }
}

internal sealed record SmtpOutboundMessage(
    string Recipient,
    string Subject,
    string PlainTextBody,
    string TransportMessageId,
    string HiveMessageId);
