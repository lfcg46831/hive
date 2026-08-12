using System.Net.Sockets;
using Hive.Domain.OccupantChannels;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed class MailKitSmtpOccupantTransport(
    IOptions<SmtpOccupantChannelOptions> options) : ISmtpOccupantTransport
{
    private readonly SmtpOccupantChannelOptions _options = options.Value;

    public async Task<OccupantChannelDeliveryResult> SendAsync(
        SmtpOutboundMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        using var client = new SmtpClient();
        try
        {
            var email = CreateMessage(message);
            await client.ConnectAsync(
                    _options.Host!,
                    _options.Port,
                    SecurityMode(_options.Security),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                await client.AuthenticateAsync(
                        _options.Username,
                        _options.Password!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await client.SendAsync(email, cancellationToken).ConfigureAwait(false);
            return OccupantChannelDeliveryResult.Succeeded();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MailKit.Security.AuthenticationException)
        {
            return Failed(OccupantChannelDeliveryErrorCode.AuthenticationFailed, retryable: false);
        }
        catch (ServiceNotAuthenticatedException)
        {
            return Failed(OccupantChannelDeliveryErrorCode.AuthenticationFailed, retryable: false);
        }
        catch (SmtpCommandException exception)
        {
            return FromStatusCode((int)exception.StatusCode);
        }
        catch (SmtpProtocolException)
        {
            return Failed(OccupantChannelDeliveryErrorCode.ChannelUnavailable, retryable: true);
        }
        catch (ServiceNotConnectedException)
        {
            return Failed(OccupantChannelDeliveryErrorCode.ChannelUnavailable, retryable: true);
        }
        catch (SslHandshakeException)
        {
            return Failed(OccupantChannelDeliveryErrorCode.ChannelUnavailable, retryable: true);
        }
        catch (IOException)
        {
            return Failed(OccupantChannelDeliveryErrorCode.ChannelUnavailable, retryable: true);
        }
        catch (SocketException)
        {
            return Failed(OccupantChannelDeliveryErrorCode.ChannelUnavailable, retryable: true);
        }
        catch (NotSupportedException)
        {
            return Failed(OccupantChannelDeliveryErrorCode.ConfigurationInvalid, retryable: false);
        }
        catch (Exception)
        {
            return Failed(OccupantChannelDeliveryErrorCode.Unknown, retryable: true);
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The functional result has already been determined. Disconnect is best effort
                    // and must neither expose transport details nor turn success into a second result.
                }
            }
        }
    }

    private MimeMessage CreateMessage(SmtpOutboundMessage message)
    {
        var email = new MimeMessage
        {
            Subject = message.Subject,
            MessageId = message.TransportMessageId,
            Body = new TextPart("plain") { Text = message.PlainTextBody },
        };
        var replyToAddress = _options.ReplyToAddress ?? _options.FromAddress!;
        email.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress!));
        email.To.Add(MailboxAddress.Parse(message.Recipient));
        email.ReplyTo.Add(MailboxAddress.Parse(replyToAddress));
        email.Headers.Add("X-Hive-Message-Id", message.HiveMessageId);
        return email;
    }

    private static SecureSocketOptions SecurityMode(string value) => value switch
    {
        SmtpSecurityModeContract.None => SecureSocketOptions.None,
        SmtpSecurityModeContract.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurityModeContract.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => throw new InvalidOperationException("SMTP security mode was not validated."),
    };

    private static OccupantChannelDeliveryResult FromStatusCode(int statusCode) =>
        statusCode switch
        {
            >= 400 and < 500 =>
                Failed(OccupantChannelDeliveryErrorCode.ChannelUnavailable, retryable: true),
            530 or 534 or 535 or 538 =>
                Failed(OccupantChannelDeliveryErrorCode.AuthenticationFailed, retryable: false),
            >= 500 and < 600 =>
                Failed(OccupantChannelDeliveryErrorCode.DeliveryRejected, retryable: false),
            _ => Failed(OccupantChannelDeliveryErrorCode.Unknown, retryable: true),
        };

    private static OccupantChannelDeliveryResult Failed(
        OccupantChannelDeliveryErrorCode code,
        bool retryable) =>
        OccupantChannelDeliveryResult.Failed(new OccupantChannelDeliveryError(code, retryable));
}
