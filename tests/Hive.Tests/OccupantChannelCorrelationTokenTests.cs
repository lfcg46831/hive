using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class OccupantChannelCorrelationTokenTests
{
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 8, 12, 9, 30, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("delivery-lead");
    private static readonly MessageId Message = MessageId.From(
        Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ThreadId Thread = ThreadId.From(
        Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly MessageId ApprovalRequestId = MessageId.From(
        Guid.Parse("33333333-3333-3333-3333-333333333333"));

    [Fact]
    public void Issued_work_reply_token_round_trips_all_authenticated_scope()
    {
        var service = Service(new MutableTimeProvider(IssuedAt));

        var token = service.Issue(Request());
        var validation = service.Validate(token.Value);

        Assert.StartsWith("hive-oc1.", token.Value, StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", token.ToString());
        Assert.True(validation.IsValid);
        var claims = validation.Claims!;
        Assert.NotEqual(Guid.Empty, claims.TokenId);
        Assert.Equal(Organization, claims.OrganizationId);
        Assert.Equal(Position, claims.PositionId);
        Assert.Equal(Message, claims.MessageId);
        Assert.Equal(Thread, claims.ThreadId);
        Assert.Null(claims.RequestId);
        Assert.False(claims.IsDecision);
        Assert.Equal(IssuedAt, claims.IssuedAtUtc);
        Assert.Equal(IssuedAt.AddDays(7), claims.ExpiresAtUtc);
    }

    [Fact]
    public void Approval_token_is_explicitly_bound_to_request_id()
    {
        var service = Service(new MutableTimeProvider(IssuedAt));

        var token = service.Issue(Request(ApprovalRequestId));
        var validation = service.Validate(token.Value);

        Assert.True(validation.IsValid);
        Assert.True(validation.Claims!.IsDecision);
        Assert.Equal(ApprovalRequestId, validation.Claims.RequestId);
    }

    [Fact]
    public void Payload_or_signature_tampering_is_rejected_before_claims_are_returned()
    {
        var service = Service(new MutableTimeProvider(IssuedAt));
        var value = service.Issue(Request()).Value;

        var payloadTampered = service.Validate(Tamper(value, segment: 1));
        var signatureTampered = service.Validate(Tamper(value, segment: 2));

        Assert.Equal(
            OccupantChannelCorrelationTokenFailure.InvalidSignature,
            payloadTampered.Failure);
        Assert.Equal(
            OccupantChannelCorrelationTokenFailure.InvalidSignature,
            signatureTampered.Failure);
        Assert.Null(payloadTampered.Claims);
        Assert.Null(signatureTampered.Claims);
    }

    [Fact]
    public void A_different_operational_key_cannot_validate_the_token()
    {
        var time = new MutableTimeProvider(IssuedAt);
        var issuer = Service(time, keySeed: 1);
        var validator = Service(time, keySeed: 2);

        var result = validator.Validate(issuer.Issue(Request()).Value);

        Assert.Equal(OccupantChannelCorrelationTokenFailure.InvalidSignature, result.Failure);
    }

    [Fact]
    public void Exact_expiration_instant_is_invalid()
    {
        var time = new MutableTimeProvider(IssuedAt);
        var service = Service(time);
        var token = service.Issue(Request());
        time.UtcNow = IssuedAt.AddDays(7);

        var result = service.Validate(token.Value);

        Assert.Equal(OccupantChannelCorrelationTokenFailure.Expired, result.Failure);
    }

    [Fact]
    public void Token_issued_in_the_future_is_rejected_deterministically()
    {
        var future = new MutableTimeProvider(IssuedAt.AddMinutes(1));
        var token = Service(future).Issue(Request());
        var validator = Service(new MutableTimeProvider(IssuedAt));

        var result = validator.Validate(token.Value);

        Assert.Equal(OccupantChannelCorrelationTokenFailure.NotYetValid, result.Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("opaque")]
    [InlineData("hive-oc1.a.b")]
    [InlineData("hive-oc1..signature")]
    public void Malformed_input_is_a_closed_result_and_never_throws(string? value)
    {
        var result = Service(new MutableTimeProvider(IssuedAt)).Validate(value);

        Assert.Equal(OccupantChannelCorrelationTokenFailure.Malformed, result.Failure);
    }

    [Fact]
    public void Unknown_envelope_version_is_rejected_explicitly()
    {
        var result = Service(new MutableTimeProvider(IssuedAt))
            .Validate("hive-oc2.payload.signature");

        Assert.Equal(OccupantChannelCorrelationTokenFailure.UnsupportedVersion, result.Failure);
    }

    [Fact]
    public async Task Concurrent_decision_redemption_accepts_exactly_one_caller()
    {
        var service = Service(new MutableTimeProvider(IssuedAt));
        var token = service.Issue(Request(ApprovalRequestId));

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(
            _ => service.RedeemDecisionAsync(token.Value).AsTask()));

        Assert.Single(results.Where(result => result.IsValid));
        Assert.Equal(
            11,
            results.Count(result =>
                result.Failure == OccupantChannelCorrelationTokenFailure.AlreadyUsed));
    }

    [Fact]
    public async Task Work_reply_validation_cannot_be_misused_as_decision_redemption()
    {
        var service = Service(new MutableTimeProvider(IssuedAt));
        var token = service.Issue(Request());

        var redemption = await service.RedeemDecisionAsync(token.Value);
        var validation = service.Validate(token.Value);

        Assert.Equal(OccupantChannelCorrelationTokenFailure.NotDecision, redemption.Failure);
        Assert.True(validation.IsValid);
    }

    [Fact]
    public async Task Unavailable_durable_store_fails_closed_without_losing_validated_claims()
    {
        var service = Service(
            new MutableTimeProvider(IssuedAt),
            store: UnavailableOccupantChannelDecisionTokenUseStore.Instance);
        var token = service.Issue(Request(ApprovalRequestId));

        var validation = service.Validate(token.Value);
        var redemption = await service.RedeemDecisionAsync(token.Value);

        Assert.True(validation.IsValid);
        Assert.Equal(
            OccupantChannelCorrelationTokenFailure.UseStoreUnavailable,
            redemption.Failure);
    }

    [Fact]
    public void Delivery_factory_renders_approval_and_issues_decision_scoped_token()
    {
        var service = Service(new MutableTimeProvider(IssuedAt));
        var factory = new SignedOccupantChannelDeliveryRequestFactory(service);
        var user = UserId.From(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var binding = OccupantChannelBindingId.From(
            Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var occupant = OccupantId.From("human:delivery-lead");
        var message = new ApprovalRequest(
            ApprovalRequestId,
            Organization,
            new PositionEndpointRef(PositionId.From("engineer")),
            new PositionEndpointRef(Position),
            Thread,
            Priority.High,
            schemaVersion: 1,
            IssuedAt,
            IssuedAt.AddDays(2),
            "deploy release",
            "production change",
            ApprovalPolicyRef.From("release-approval"));

        var request = factory.Create(new OccupantChannelDeliveryContext(
            Organization,
            Position,
            occupant,
            user,
            binding,
            message));

        Assert.Equal(binding, request.OccupantChannelBindingId);
        Assert.Contains("Type: ApprovalRequest", request.RenderedMessage, StringComparison.Ordinal);
        Assert.Contains("deploy release", request.RenderedMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(user.Value.ToString("D"), request.RenderedMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(binding.Value.ToString("D"), request.RenderedMessage, StringComparison.Ordinal);
        var claims = service.Validate(request.CorrelationToken.Value).Claims!;
        Assert.Equal(ApprovalRequestId, claims.MessageId);
        Assert.Equal(ApprovalRequestId, claims.RequestId);
        Assert.Equal(Thread, claims.ThreadId);
    }

    private static OccupantChannelCorrelationTokenRequest Request(MessageId? requestId = null) =>
        new(Organization, Position, Message, Thread, requestId);

    private static HmacOccupantChannelCorrelationTokenService Service(
        TimeProvider timeProvider,
        int keySeed = 1,
        IOccupantChannelDecisionTokenUseStore? store = null) =>
        new(
            Options.Create(new OccupantChannelCorrelationTokenOptions
            {
                SigningKey = SigningKey(keySeed),
                Lifetime = TimeSpan.FromDays(7),
            }),
            timeProvider,
            store ?? new InMemoryOccupantChannelDecisionTokenUseStore());

    internal static string SigningKey(int seed = 1) => Convert.ToBase64String(
        Enumerable.Range(seed, 32).Select(value => checked((byte)value)).ToArray());

    private static string Tamper(string value, int segment)
    {
        var segments = value.Split('.');
        var characters = segments[segment].ToCharArray();
        characters[0] = characters[0] == 'A' ? 'B' : 'A';
        segments[segment] = new string(characters);
        return string.Join('.', segments);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
