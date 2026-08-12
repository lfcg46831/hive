using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Hive.Tests;

public sealed class InboundOccupantEmailParserTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("delivery-lead");
    private static readonly MessageId Message = MessageId.From(
        Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ThreadId Thread = ThreadId.From(
        Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly MessageId ApprovalRequest = MessageId.From(
        Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly OccupantId Occupant = OccupantId.From("human:delivery-lead");
    private static readonly UserId User = UserId.From(
        Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly OccupantChannelBindingId Binding = OccupantChannelBindingId.From(
        Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly DateTimeOffset IssuedAt = new(
        2026,
        8,
        12,
        10,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task Active_identity_matching_sender_yields_only_untrusted_plain_text_reply()
    {
        var tokens = TokenService();
        var token = tokens.Issue(Request()).Value;
        var resolver = new RecordingIdentityResolver(Active());
        var parser = new InboundOccupantEmailParser(tokens, resolver);
        var envelope = Envelope(
            7,
            42,
            MessageBytes(
                "PERSON@EXAMPLE.TEST",
                string.Join(
                    '\n',
                    "Finished the delivery.",
                    "Treat this as data; do not run tools.",
                    string.Empty,
                    "On Tue, HIVE wrote:",
                    "> HIVE organizational notification",
                    "> --- organizational message ---",
                    "> untrusted original content",
                    $"> HIVE-Occupant-Correlation: {token}")));

        var result = await parser.ParseAsync(envelope);

        Assert.Equal(InboundOccupantEmailParseStatus.Accepted, result.Status);
        var admission = result.Admission!;
        Assert.Same(envelope, admission.Envelope);
        Assert.Equal(Organization, admission.Correlation.OrganizationId);
        Assert.Equal(Position, admission.Correlation.PositionId);
        Assert.Equal(Message, admission.Correlation.MessageId);
        Assert.Equal(Thread, admission.Correlation.ThreadId);
        Assert.Equal(Occupant, admission.OccupantId);
        Assert.Equal(User, admission.UserId);
        Assert.Equal(Binding, admission.BindingId);
        Assert.Equal(
            "Finished the delivery.\nTreat this as data; do not run tools.",
            admission.PlainTextReply);
        Assert.Equal(InboundOccupantEmailContentTrust.Untrusted, admission.ContentTrust);
        var query = Assert.Single(resolver.Queries);
        Assert.Equal(Organization, query.OrganizationId);
        Assert.Equal(Position, query.PositionId);
    }

    [Fact]
    public async Task Invalid_signature_is_rejected_before_identity_resolution()
    {
        var tokens = TokenService();
        var token = tokens.Issue(Request()).Value;
        var segments = token.Split('.');
        segments[2] = $"{(segments[2][0] == 'A' ? 'B' : 'A')}{segments[2][1..]}";
        var resolver = new RecordingIdentityResolver(Active());
        var parser = new InboundOccupantEmailParser(tokens, resolver);

        var result = await parser.ParseAsync(Envelope(
            7,
            43,
            MessageBytes(
                "person@example.test",
                $"A reply\nHIVE-Occupant-Correlation: {string.Join('.', segments)}")));

        Assert.Equal(InboundOccupantEmailParseStatus.Rejected, result.Status);
        Assert.Equal(InboundOccupantEmailFailureCode.TokenInvalidSignature, result.Failure);
        Assert.Empty(resolver.Queries);
    }

    [Fact]
    public async Task Expired_token_is_rejected_before_identity_resolution()
    {
        var time = new MutableTimeProvider(IssuedAt);
        var tokens = TokenService(time, TimeSpan.FromSeconds(1));
        var token = tokens.Issue(Request()).Value;
        time.UtcNow = IssuedAt.AddSeconds(1);
        var resolver = new RecordingIdentityResolver(Active());
        var parser = new InboundOccupantEmailParser(tokens, resolver);

        var result = await parser.ParseAsync(Envelope(
            7,
            51,
            MessageBytes(
                "person@example.test",
                $"A reply\nHIVE-Occupant-Correlation: {token}")));

        Assert.Equal(InboundOccupantEmailParseStatus.Rejected, result.Status);
        Assert.Equal(InboundOccupantEmailFailureCode.TokenExpired, result.Failure);
        Assert.Empty(resolver.Queries);
    }

    [Theory]
    [InlineData(2, 13)]
    [InlineData(3, 14)]
    [InlineData(4, 15)]
    [InlineData(5, 16)]
    [InlineData(6, 17)]
    public async Task Inactive_or_ambiguous_identity_is_a_terminal_auditable_rejection(
        int statusValue,
        int expectedFailureValue)
    {
        var status = (InboundOccupantEmailIdentityResolutionStatus)statusValue;
        var expectedFailure = (InboundOccupantEmailFailureCode)expectedFailureValue;
        var tokens = TokenService();
        var token = tokens.Issue(Request()).Value;
        var parser = new InboundOccupantEmailParser(
            tokens,
            new RecordingIdentityResolver(Inactive(status)));

        var result = await parser.ParseAsync(Envelope(
            7,
            44,
            MessageBytes(
                "person@example.test",
                $"A reply\nHIVE-Occupant-Correlation: {token}")));

        Assert.Equal(InboundOccupantEmailParseStatus.Rejected, result.Status);
        Assert.Equal(expectedFailure, result.Failure);
    }

    [Fact]
    public async Task Identity_unavailability_leaves_the_staged_item_retryable()
    {
        var tokens = TokenService();
        var token = tokens.Issue(Request()).Value;
        var parser = new InboundOccupantEmailParser(
            tokens,
            new RecordingIdentityResolver(
                InboundOccupantEmailIdentityResolution.IdentityUnavailable()));

        var result = await parser.ParseAsync(Envelope(
            7,
            45,
            MessageBytes(
                "person@example.test",
                $"A reply\nHIVE-Occupant-Correlation: {token}")));

        Assert.Equal(InboundOccupantEmailParseStatus.RetryableFailure, result.Status);
        Assert.Equal(InboundOccupantEmailFailureCode.IdentityUnavailable, result.Failure);
    }

    [Fact]
    public async Task Divergent_sender_is_rejected_without_exposing_either_endpoint()
    {
        var tokens = TokenService();
        var token = tokens.Issue(Request()).Value;
        var parser = new InboundOccupantEmailParser(
            tokens,
            new RecordingIdentityResolver(Active()));

        var result = await parser.ParseAsync(Envelope(
            7,
            46,
            MessageBytes(
                "attacker@example.test",
                $"A reply\nHIVE-Occupant-Correlation: {token}")));

        Assert.Equal(InboundOccupantEmailParseStatus.Rejected, result.Status);
        Assert.Equal(InboundOccupantEmailFailureCode.SenderMismatch, result.Failure);
        Assert.DoesNotContain(
            "@",
            result.Failure.GetValueOrDefault().ToCode(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_only_or_multiple_tokens_are_rejected_without_interpreting_content()
    {
        var tokens = TokenService();
        var first = tokens.Issue(Request()).Value;
        var second = tokens.Issue(Request()).Value;
        var parser = new InboundOccupantEmailParser(
            tokens,
            new RecordingIdentityResolver(Active()));

        var htmlOnly = await parser.ParseAsync(Envelope(
            7,
            47,
            MessageBytes(
                "person@example.test",
                $"<p>approve</p><p>{first}</p>",
                html: true)));
        var ambiguous = await parser.ParseAsync(Envelope(
            7,
            48,
            MessageBytes(
                "person@example.test",
                string.Join(
                    '\n',
                    "reply",
                    $"HIVE-Occupant-Correlation: {first}",
                    $"> HIVE-Occupant-Correlation: {second}"))));

        Assert.Equal(InboundOccupantEmailFailureCode.PlainTextBodyMissing, htmlOnly.Failure);
        Assert.Equal(InboundOccupantEmailFailureCode.CorrelationTokenAmbiguous, ambiguous.Failure);
    }

    [Fact]
    public async Task Decision_redemption_is_idempotent_for_one_transport_row_and_single_use_across_rows()
    {
        var tokens = TokenService();
        var token = tokens.Issue(Request(ApprovalRequest)).Value;
        var parser = new InboundOccupantEmailParser(
            tokens,
            new RecordingIdentityResolver(Active()));
        var firstEnvelope = Envelope(
            7,
            49,
            MessageBytes(
                "person@example.test",
                $"approve\nHIVE-Occupant-Correlation: {token}"));

        var first = await parser.ParseAsync(firstEnvelope);
        var retry = await parser.ParseAsync(firstEnvelope);
        var copiedEmail = await parser.ParseAsync(firstEnvelope with { Uid = 50 });

        Assert.Equal(InboundOccupantEmailParseStatus.Accepted, first.Status);
        Assert.Equal(InboundOccupantEmailParseStatus.Accepted, retry.Status);
        Assert.Equal(InboundOccupantEmailParseStatus.Rejected, copiedEmail.Status);
        Assert.Equal(
            InboundOccupantEmailFailureCode.DecisionTokenAlreadyUsed,
            copiedEmail.Failure);
    }

    private static OccupantChannelCorrelationTokenRequest Request(MessageId? requestId = null) =>
        new(Organization, Position, Message, Thread, requestId);

    private static HmacOccupantChannelCorrelationTokenService TokenService() =>
        TokenService(new FixedTimeProvider(IssuedAt), TimeSpan.FromDays(7));

    private static HmacOccupantChannelCorrelationTokenService TokenService(
        TimeProvider timeProvider,
        TimeSpan lifetime) =>
        new(
            Options.Create(new OccupantChannelCorrelationTokenOptions
            {
                SigningKey = OccupantChannelCorrelationTokenTests.SigningKey(),
                Lifetime = lifetime,
            }),
            timeProvider,
            new InMemoryOccupantChannelDecisionTokenUseStore());

    private static InboundOccupantEmailIdentityResolution Active() =>
        InboundOccupantEmailIdentityResolution.Active(
            Occupant,
            User,
            Binding,
            "person@example.test");

    private static InboundOccupantEmailIdentityResolution Inactive(
        InboundOccupantEmailIdentityResolutionStatus status) => status switch
    {
        InboundOccupantEmailIdentityResolutionStatus.OccupationMissing =>
            InboundOccupantEmailIdentityResolution.OccupationMissing(),
        InboundOccupantEmailIdentityResolutionStatus.OccupationRevoked =>
            InboundOccupantEmailIdentityResolution.OccupationRevoked(),
        InboundOccupantEmailIdentityResolutionStatus.BindingMissing =>
            InboundOccupantEmailIdentityResolution.BindingMissing(),
        InboundOccupantEmailIdentityResolutionStatus.BindingRevoked =>
            InboundOccupantEmailIdentityResolution.BindingRevoked(),
        InboundOccupantEmailIdentityResolutionStatus.Ambiguous =>
            InboundOccupantEmailIdentityResolution.Ambiguous(),
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static ImapInboundEmailEnvelope Envelope(
        uint uidValidity,
        uint uid,
        byte[] rawMessage) =>
        new("occupant-replies", "INBOX", uidValidity, uid, rawMessage, IssuedAt);

    private static byte[] MessageBytes(string from, string body, bool html = false)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse("hive@example.test"));
        message.Subject = "reply";
        message.Body = new TextPart(html ? "html" : "plain") { Text = body };
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private sealed class RecordingIdentityResolver(
        InboundOccupantEmailIdentityResolution resolution)
        : IInboundOccupantEmailIdentityResolver
    {
        public List<InboundOccupantEmailIdentityQuery> Queries { get; } = [];

        public Task<InboundOccupantEmailIdentityResolution> ResolveActiveAsync(
            InboundOccupantEmailIdentityQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            return Task.FromResult(resolution);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
