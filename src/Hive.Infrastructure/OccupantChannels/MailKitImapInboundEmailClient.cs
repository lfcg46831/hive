using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed class MailKitImapInboundEmailClient(
    IOptions<ImapInboundEmailOptions> options) : IImapInboundEmailClient
{
    private const int CopyBufferSize = 81920;
    private readonly ImapInboundEmailOptions _options = options.Value;

    public async Task<ImapInboundEmailBatch> FetchBatchAsync(
        ImapInboundEmailCheckpoint? checkpoint,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(_options.OperationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        var effectiveCancellation = linkedCts.Token;

        using var client = new ImapClient
        {
            Timeout = checked((int)Math.Min(
                _options.OperationTimeout.TotalMilliseconds,
                int.MaxValue)),
        };

        try
        {
            await client.ConnectAsync(
                _options.Host!,
                _options.Port,
                ToSecureSocketOptions(_options.Security),
                effectiveCancellation).ConfigureAwait(false);
            await client.AuthenticateAsync(
                _options.Username!,
                _options.Password!,
                effectiveCancellation).ConfigureAwait(false);

            var folder = await client
                .GetFolderAsync(_options.Mailbox, effectiveCancellation)
                .ConfigureAwait(false);
            await folder
                .OpenAsync(FolderAccess.ReadOnly, effectiveCancellation)
                .ConfigureAwait(false);

            var uidValidity = folder.UidValidity;
            if (uidValidity == 0)
            {
                throw new InvalidOperationException("The IMAP mailbox reported UIDVALIDITY zero.");
            }

            var baseline = checkpoint is not null && checkpoint.UidValidity == uidValidity
                ? checkpoint.LastUid
                : 0;
            var selected = baseline == uint.MaxValue
                ? []
                : (await folder
                    .SearchAsync(
                        SearchQuery.Uids(new UniqueIdRange(
                            new UniqueId(baseline + 1),
                            UniqueId.MaxValue)),
                        effectiveCancellation)
                    .ConfigureAwait(false))
                .OrderBy(uid => uid.Id)
                .Take(_options.BatchSize)
                .ToArray();

            var messages = new List<FetchedImapMessage>(selected.Length);
            foreach (var uid in selected)
            {
                await using var stream = await folder
                    .GetStreamAsync(uid, effectiveCancellation)
                    .ConfigureAwait(false);
                var rawMessage = await ReadBoundedAsync(
                        stream,
                        uid.Id,
                        effectiveCancellation)
                    .ConfigureAwait(false);
                messages.Add(new FetchedImapMessage(uid.Id, rawMessage));
            }

            return new ImapInboundEmailBatch(
                _options.SourceId,
                _options.Mailbox,
                uidValidity,
                messages.Count == 0 ? baseline : messages[^1].Uid,
                messages);
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The fetch result or original transport error owns the outcome. Disconnect is
                    // best-effort and must not replace it with a second, less useful exception.
                }
            }
        }
    }

    private async Task<byte[]> ReadBoundedAsync(
        Stream source,
        uint uid,
        CancellationToken cancellationToken)
    {
        if (source.CanSeek && source.Length > _options.MaxMessageBytes)
        {
            throw new ImapInboundEmailTooLargeException(
                _options.SourceId,
                _options.Mailbox,
                uid,
                source.Length,
                _options.MaxMessageBytes);
        }

        using var destination = new MemoryStream();
        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = await source
                   .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            if (destination.Length + read > _options.MaxMessageBytes)
            {
                throw new ImapInboundEmailTooLargeException(
                    _options.SourceId,
                    _options.Mailbox,
                    uid,
                    destination.Length + read,
                    _options.MaxMessageBytes);
            }

            await destination
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return destination.ToArray();
    }

    private static SecureSocketOptions ToSecureSocketOptions(string security) => security switch
    {
        ImapSecurityModeContract.None => SecureSocketOptions.None,
        ImapSecurityModeContract.StartTls => SecureSocketOptions.StartTls,
        ImapSecurityModeContract.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => throw new InvalidOperationException("IMAP security mode was not validated."),
    };
}

internal sealed class ImapInboundEmailTooLargeException : Exception
{
    public ImapInboundEmailTooLargeException(
        string sourceId,
        string mailbox,
        uint uid,
        long observedBytes,
        int maximumBytes)
        : base(
            $"IMAP source '{sourceId}' mailbox '{mailbox}' UID {uid} exceeds the configured maximum message size.")
    {
        SourceId = sourceId;
        Mailbox = mailbox;
        Uid = uid;
        ObservedBytes = observedBytes;
        MaximumBytes = maximumBytes;
    }

    public string SourceId { get; }

    public string Mailbox { get; }

    public uint Uid { get; }

    public long ObservedBytes { get; }

    public int MaximumBytes { get; }
}
