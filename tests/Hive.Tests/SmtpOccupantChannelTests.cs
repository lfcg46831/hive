using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class SmtpOccupantChannelTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("delivery-lead");
    private static readonly OccupantId Occupant = OccupantId.From("human:delivery-lead");
    private static readonly UserId User = UserId.From(
        Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly OccupantChannelBindingId Binding = OccupantChannelBindingId.From(
        Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly MessageId Message = MessageId.From(
        Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly ThreadId Thread = ThreadId.From(
        Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [Fact]
    public async Task Active_binding_is_resolved_at_delivery_and_rendered_as_plain_text()
    {
        var resolver = new SequenceBindingResolver(
            OccupantEmailBindingResolution.Active("person@example.test"));
        var transport = new SequenceTransport(OccupantChannelDeliveryResult.Succeeded());
        var delay = new RecordingDelay();
        var channel = CreateChannel(resolver, transport, delay);

        var result = await channel.DeliverAsync(Request());

        Assert.True(result.IsSuccess);
        var query = Assert.Single(resolver.Queries);
        Assert.Equal(Organization, query.OrganizationId);
        Assert.Equal(Position, query.PositionId);
        Assert.Equal(Occupant, query.OccupantId);
        Assert.Equal(User, query.UserId);
        Assert.Equal(Binding, query.BindingId);

        var email = Assert.Single(transport.Messages);
        Assert.Equal("person@example.test", email.Recipient);
        Assert.Contains("Position notification", email.Subject, StringComparison.Ordinal);
        Assert.Contains("The position inbox remains the source of truth", email.PlainTextBody);
        Assert.Contains("A rendered organizational message.", email.PlainTextBody);
        Assert.Contains("Reply in plain text", email.PlainTextBody);
        Assert.Contains("HIVE-Occupant-Correlation: opaque.signed.token", email.PlainTextBody);
        Assert.DoesNotContain("<html", email.PlainTextBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"hive-{Message.Value:N}@example.test", email.TransportMessageId);
        Assert.Equal(Message.Value.ToString("D"), email.HiveMessageId);
        Assert.Empty(delay.Delays);
    }

    [Theory]
    [InlineData(false, OccupantChannelDeliveryErrorCode.BindingUnavailable)]
    [InlineData(true, OccupantChannelDeliveryErrorCode.BindingRevoked)]
    public async Task Missing_or_revoked_binding_fails_closed_without_smtp(
        bool revoked,
        OccupantChannelDeliveryErrorCode expectedCode)
    {
        var resolution = revoked
            ? OccupantEmailBindingResolution.Revoked()
            : OccupantEmailBindingResolution.Missing();
        var resolver = new SequenceBindingResolver(resolution);
        var transport = new SequenceTransport(OccupantChannelDeliveryResult.Succeeded());
        var delay = new RecordingDelay();
        var channel = CreateChannel(resolver, transport, delay);

        var result = await channel.DeliverAsync(Request());

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.Empty(transport.Messages);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task Retry_uses_exponential_bounded_backoff_and_resolves_binding_each_time()
    {
        var resolver = new SequenceBindingResolver(
            OccupantEmailBindingResolution.Active("person@example.test"));
        var transport = new SequenceTransport(
            Failed(OccupantChannelDeliveryErrorCode.ChannelUnavailable, retryable: true),
            Failed(OccupantChannelDeliveryErrorCode.Timeout, retryable: true),
            OccupantChannelDeliveryResult.Succeeded());
        var delay = new RecordingDelay();
        var options = ValidOptions();
        options.InitialBackoff = TimeSpan.FromMilliseconds(25);
        options.MaxBackoff = TimeSpan.FromMilliseconds(40);
        var channel = CreateChannel(resolver, transport, delay, options);

        var result = await channel.DeliverAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Equal(3, resolver.Queries.Count);
        Assert.Equal(3, transport.Messages.Count);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(40)],
            delay.Delays);
        Assert.Single(transport.Messages.Select(message => message.TransportMessageId).Distinct());
    }

    [Fact]
    public async Task Revocation_during_backoff_prevents_the_next_smtp_attempt()
    {
        var resolver = new SequenceBindingResolver(
            OccupantEmailBindingResolution.Active("person@example.test"),
            OccupantEmailBindingResolution.Revoked());
        var transport = new SequenceTransport(
            Failed(OccupantChannelDeliveryErrorCode.ChannelUnavailable, retryable: true));
        var delay = new RecordingDelay();
        var channel = CreateChannel(resolver, transport, delay);

        var result = await channel.DeliverAsync(Request());

        Assert.Equal(OccupantChannelDeliveryErrorCode.BindingRevoked, result.Error!.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.Equal(2, resolver.Queries.Count);
        Assert.Single(transport.Messages);
        Assert.Single(delay.Delays);
    }

    [Fact]
    public async Task Invalid_personal_endpoint_fails_without_exposing_it_to_transport()
    {
        var resolver = new SequenceBindingResolver(
            OccupantEmailBindingResolution.Active("Display Name <person@example.test>"));
        var transport = new SequenceTransport(OccupantChannelDeliveryResult.Succeeded());
        var channel = CreateChannel(resolver, transport, new RecordingDelay());

        var result = await channel.DeliverAsync(Request());

        Assert.Equal(OccupantChannelDeliveryErrorCode.ConfigurationInvalid, result.Error!.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.Empty(transport.Messages);
    }

    [Fact]
    public async Task Non_retryable_smtp_failure_is_returned_after_one_attempt()
    {
        var resolver = new SequenceBindingResolver(
            OccupantEmailBindingResolution.Active("person@example.test"));
        var transport = new SequenceTransport(
            Failed(OccupantChannelDeliveryErrorCode.AuthenticationFailed, retryable: false));
        var delay = new RecordingDelay();
        var channel = CreateChannel(resolver, transport, delay);

        var result = await channel.DeliverAsync(Request());

        Assert.Equal(OccupantChannelDeliveryErrorCode.AuthenticationFailed, result.Error!.Code);
        Assert.Single(resolver.Queries);
        Assert.Single(transport.Messages);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task Attempt_timeout_is_structured_and_does_not_wait_for_a_stalled_transport()
    {
        var resolver = new SequenceBindingResolver(
            OccupantEmailBindingResolution.Active("person@example.test"));
        var transport = new StalledTransport();
        var options = ValidOptions();
        options.MaxAttempts = 1;
        options.AttemptTimeout = TimeSpan.FromMilliseconds(20);
        var channel = CreateChannel(resolver, transport, new RecordingDelay(), options);

        var result = await channel.DeliverAsync(Request());

        Assert.Equal(OccupantChannelDeliveryErrorCode.Timeout, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.Equal(1, transport.SendCount);
    }

    private static SmtpOccupantChannel CreateChannel(
        IOccupantEmailBindingResolver resolver,
        ISmtpOccupantTransport transport,
        ISmtpRetryDelay delay,
        SmtpOccupantChannelOptions? options = null) =>
        new(
            resolver,
            transport,
            new SmtpOccupantEmailRenderer(),
            delay,
            Options.Create(options ?? ValidOptions()));

    private static SmtpOccupantChannelOptions ValidOptions() => new()
    {
        Enabled = true,
        Host = "smtp.example.test",
        Port = 587,
        Security = "start-tls",
        FromAddress = "hive@example.test",
        FromName = "HIVE",
        ReplyToAddress = "replies@example.test",
        SubjectPrefix = "[HIVE]",
        MaxAttempts = 3,
        InitialBackoff = TimeSpan.FromMilliseconds(1),
        MaxBackoff = TimeSpan.FromMilliseconds(4),
        AttemptTimeout = TimeSpan.FromSeconds(1),
    };

    private static OccupantChannelDeliveryRequest Request() => new(
        Organization,
        Position,
        Occupant,
        User,
        Binding,
        Message,
        Thread,
        "A rendered organizational message.",
        OccupantChannelCorrelationToken.From("opaque.signed.token"));

    private static OccupantChannelDeliveryResult Failed(
        OccupantChannelDeliveryErrorCode code,
        bool retryable) =>
        OccupantChannelDeliveryResult.Failed(new OccupantChannelDeliveryError(code, retryable));

    private sealed class SequenceBindingResolver : IOccupantEmailBindingResolver
    {
        private readonly Queue<OccupantEmailBindingResolution> _resolutions;
        private OccupantEmailBindingResolution _last;

        public SequenceBindingResolver(params OccupantEmailBindingResolution[] resolutions)
        {
            _resolutions = new Queue<OccupantEmailBindingResolution>(resolutions);
            _last = resolutions[^1];
        }

        public List<OccupantEmailBindingQuery> Queries { get; } = [];

        public Task<OccupantEmailBindingResolution> ResolveActiveAsync(
            OccupantEmailBindingQuery query,
            CancellationToken cancellationToken)
        {
            Queries.Add(query);
            cancellationToken.ThrowIfCancellationRequested();
            if (_resolutions.TryDequeue(out var resolution))
            {
                _last = resolution;
            }

            return Task.FromResult(_last);
        }
    }

    private sealed class SequenceTransport(params OccupantChannelDeliveryResult[] results)
        : ISmtpOccupantTransport
    {
        private readonly Queue<OccupantChannelDeliveryResult> _results = new(results);
        private OccupantChannelDeliveryResult _last = results[^1];

        public List<SmtpOutboundMessage> Messages { get; } = [];

        public Task<OccupantChannelDeliveryResult> SendAsync(
            SmtpOutboundMessage message,
            CancellationToken cancellationToken)
        {
            Messages.Add(message);
            cancellationToken.ThrowIfCancellationRequested();
            if (_results.TryDequeue(out var result))
            {
                _last = result;
            }

            return Task.FromResult(_last);
        }
    }

    private sealed class RecordingDelay : ISmtpRetryDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class StalledTransport : ISmtpOccupantTransport
    {
        public int SendCount { get; private set; }

        public async Task<OccupantChannelDeliveryResult> SendAsync(
            SmtpOutboundMessage message,
            CancellationToken cancellationToken)
        {
            SendCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return OccupantChannelDeliveryResult.Succeeded();
        }
    }
}
