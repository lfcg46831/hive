using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.Identity;
using MimeKit;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed class InboundOccupantEmailParser(
    IOccupantChannelCorrelationTokenService correlationTokens,
    IInboundOccupantEmailIdentityResolver identityResolver) : IInboundOccupantEmailParser
{
    private const string CorrelationHeader = "HIVE-Occupant-Correlation:";
    private const string NotificationHeader = "HIVE organizational notification";

    public async Task<InboundOccupantEmailParseResult> ParseAsync(
        ImapInboundEmailEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        MimeMessage message;
        try
        {
            using var stream = new MemoryStream(envelope.RawMessage, writable: false);
            message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FormatException or IOException)
        {
            return InboundOccupantEmailParseResult.Rejected(
                InboundOccupantEmailFailureCode.MalformedMessage);
        }

        var senders = message.From.Mailboxes.ToArray();
        if (senders.Length == 0)
        {
            return InboundOccupantEmailParseResult.Rejected(
                InboundOccupantEmailFailureCode.SenderMissing);
        }

        if (senders.Length != 1)
        {
            return InboundOccupantEmailParseResult.Rejected(
                InboundOccupantEmailFailureCode.SenderAmbiguous);
        }

        var plainText = message.TextBody;
        if (plainText is null)
        {
            return InboundOccupantEmailParseResult.Rejected(
                InboundOccupantEmailFailureCode.PlainTextBodyMissing);
        }

        var extraction = ExtractReply(plainText);
        if (extraction.Failure is { } extractionFailure)
        {
            return InboundOccupantEmailParseResult.Rejected(extractionFailure);
        }

        var token = extraction.Token!;
        var validation = correlationTokens.Validate(token);
        if (!validation.IsValid)
        {
            return InboundOccupantEmailParseResult.Rejected(MapTokenFailure(validation.Failure!.Value));
        }

        var claims = validation.Claims!;
        InboundOccupantEmailIdentityResolution identity;
        try
        {
            identity = await identityResolver.ResolveActiveAsync(
                new InboundOccupantEmailIdentityQuery(
                    claims.OrganizationId,
                    claims.PositionId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return InboundOccupantEmailParseResult.Retryable(
                InboundOccupantEmailFailureCode.IdentityUnavailable);
        }

        var identityFailure = MapIdentityFailure(identity.Status);
        if (identityFailure is { } mappedIdentityFailure)
        {
            return identity.Status is InboundOccupantEmailIdentityResolutionStatus.IdentityUnavailable
                ? InboundOccupantEmailParseResult.Retryable(mappedIdentityFailure)
                : InboundOccupantEmailParseResult.Rejected(mappedIdentityFailure);
        }

        if (identity.OccupantId is null || identity.UserId is null || identity.BindingId is null
            || !TryNormalizeAddress(identity.NormalizedEndpoint, out var expectedSender))
        {
            return InboundOccupantEmailParseResult.Retryable(
                InboundOccupantEmailFailureCode.IdentityUnavailable);
        }

        if (!TryNormalizeAddress(senders[0].Address, out var actualSender)
            || !string.Equals(expectedSender, actualSender, StringComparison.OrdinalIgnoreCase)
            || message.Sender is { } senderHeader
            && (!TryNormalizeAddress(senderHeader.Address, out var actualSenderHeader)
                || !string.Equals(
                    expectedSender,
                    actualSenderHeader,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return InboundOccupantEmailParseResult.Rejected(
                InboundOccupantEmailFailureCode.SenderMismatch);
        }

        if (claims.IsDecision)
        {
            var redemption = await correlationTokens.RedeemDecisionAsync(
                token,
                RedemptionOperationId(envelope),
                cancellationToken).ConfigureAwait(false);
            if (!redemption.IsValid)
            {
                var redemptionFailure = MapTokenFailure(redemption.Failure!.Value);
                return redemption.Failure is OccupantChannelCorrelationTokenFailure.UseStoreUnavailable
                    ? InboundOccupantEmailParseResult.Retryable(redemptionFailure)
                    : InboundOccupantEmailParseResult.Rejected(redemptionFailure);
            }
        }

        return InboundOccupantEmailParseResult.Accepted(new InboundOccupantEmailAdmission(
            envelope,
            claims,
            identity.OccupantId,
            identity.UserId,
            identity.BindingId,
            extraction.Reply!,
            InboundOccupantEmailContentTrust.Untrusted));
    }

    private static ReplyExtraction ExtractReply(string plainText)
    {
        var lines = plainText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var tokenLines = new List<(int Index, string Token)>();
        for (var index = 0; index < lines.Length; index++)
        {
            var candidate = RemoveQuotePrefix(lines[index]).Trim();
            if (candidate.StartsWith(CorrelationHeader, StringComparison.Ordinal))
            {
                tokenLines.Add((
                    index,
                    candidate[CorrelationHeader.Length..].Trim()));
            }
        }

        if (tokenLines.Count == 0)
        {
            return ReplyExtraction.Failed(InboundOccupantEmailFailureCode.CorrelationTokenMissing);
        }

        if (tokenLines.Count != 1)
        {
            return ReplyExtraction.Failed(InboundOccupantEmailFailureCode.CorrelationTokenAmbiguous);
        }

        var tokenLine = tokenLines[0];
        if (tokenLine.Token.Length == 0)
        {
            return ReplyExtraction.Failed(InboundOccupantEmailFailureCode.TokenMalformed);
        }

        var replyEnd = tokenLine.Index;
        for (var index = 0; index < tokenLine.Index; index++)
        {
            var normalized = RemoveQuotePrefix(lines[index]).Trim();
            if (IsQuoted(lines[index])
                || string.Equals(normalized, NotificationHeader, StringComparison.Ordinal))
            {
                replyEnd = index;
                if (index > 0 && LooksLikeReplyAttribution(lines[index - 1]))
                {
                    replyEnd--;
                }

                break;
            }
        }

        var reply = string.Join('\n', lines.Take(replyEnd)).Trim();
        return reply.Length == 0
            ? ReplyExtraction.Failed(InboundOccupantEmailFailureCode.PlainTextReplyMissing)
            : ReplyExtraction.Succeeded(tokenLine.Token, reply);
    }

    private static bool IsQuoted(string line) => line.TrimStart().StartsWith('>');

    private static string RemoveQuotePrefix(string line)
    {
        var value = line.AsSpan().TrimStart();
        while (!value.IsEmpty && value[0] == '>')
        {
            value = value[1..].TrimStart();
        }

        return value.ToString();
    }

    private static bool LooksLikeReplyAttribution(string line)
    {
        var value = line.Trim();
        return value.StartsWith("On ", StringComparison.OrdinalIgnoreCase)
            && value.EndsWith("wrote:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeAddress(string? address, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(address)
            || !MailboxAddress.TryParse(address, out var mailbox))
        {
            return false;
        }

        var value = mailbox.Address.Trim();
        var separator = value.LastIndexOf('@');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        try
        {
            var local = value[..separator].Normalize(NormalizationForm.FormKC);
            var domain = new IdnMapping()
                .GetAscii(value[(separator + 1)..])
                .ToLowerInvariant();
            normalized = $"{local}@{domain}";
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Guid RedemptionOperationId(ImapInboundEmailEnvelope envelope)
    {
        var canonical = string.Join(
            '|',
            $"{envelope.SourceId.Length}:{envelope.SourceId}",
            $"{envelope.Mailbox.Length}:{envelope.Mailbox}",
            envelope.UidValidity.ToString(CultureInfo.InvariantCulture),
            envelope.Uid.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static InboundOccupantEmailFailureCode MapTokenFailure(
        OccupantChannelCorrelationTokenFailure failure) => failure switch
    {
        OccupantChannelCorrelationTokenFailure.Malformed =>
            InboundOccupantEmailFailureCode.TokenMalformed,
        OccupantChannelCorrelationTokenFailure.UnsupportedVersion =>
            InboundOccupantEmailFailureCode.TokenUnsupportedVersion,
        OccupantChannelCorrelationTokenFailure.InvalidSignature =>
            InboundOccupantEmailFailureCode.TokenInvalidSignature,
        OccupantChannelCorrelationTokenFailure.NotYetValid =>
            InboundOccupantEmailFailureCode.TokenNotYetValid,
        OccupantChannelCorrelationTokenFailure.Expired =>
            InboundOccupantEmailFailureCode.TokenExpired,
        OccupantChannelCorrelationTokenFailure.AlreadyUsed =>
            InboundOccupantEmailFailureCode.DecisionTokenAlreadyUsed,
        OccupantChannelCorrelationTokenFailure.UseStoreUnavailable =>
            InboundOccupantEmailFailureCode.DecisionTokenStoreUnavailable,
        OccupantChannelCorrelationTokenFailure.NotDecision =>
            InboundOccupantEmailFailureCode.TokenMalformed,
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown token failure."),
    };

    private static InboundOccupantEmailFailureCode? MapIdentityFailure(
        InboundOccupantEmailIdentityResolutionStatus status) => status switch
    {
        InboundOccupantEmailIdentityResolutionStatus.Active => null,
        InboundOccupantEmailIdentityResolutionStatus.OccupationMissing =>
            InboundOccupantEmailFailureCode.OccupationMissing,
        InboundOccupantEmailIdentityResolutionStatus.OccupationRevoked =>
            InboundOccupantEmailFailureCode.OccupationRevoked,
        InboundOccupantEmailIdentityResolutionStatus.BindingMissing =>
            InboundOccupantEmailFailureCode.BindingMissing,
        InboundOccupantEmailIdentityResolutionStatus.BindingRevoked =>
            InboundOccupantEmailFailureCode.BindingRevoked,
        InboundOccupantEmailIdentityResolutionStatus.Ambiguous =>
            InboundOccupantEmailFailureCode.IdentityAmbiguous,
        InboundOccupantEmailIdentityResolutionStatus.IdentityUnavailable =>
            InboundOccupantEmailFailureCode.IdentityUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown identity status."),
    };

    private sealed record ReplyExtraction(
        string? Token,
        string? Reply,
        InboundOccupantEmailFailureCode? Failure)
    {
        public static ReplyExtraction Succeeded(string token, string reply) =>
            new(token, reply, Failure: null);

        public static ReplyExtraction Failed(InboundOccupantEmailFailureCode failure) =>
            new(Token: null, Reply: null, failure);
    }
}
