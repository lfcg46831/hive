using System.Reflection;
using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;

namespace Hive.Tests;

public sealed class OccupantChannelContractTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("delivery-lead");
    private static readonly OccupantId Occupant = OccupantId.From("human-occupant");
    private static readonly UserId User =
        UserId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly OccupantChannelBindingId Binding =
        OccupantChannelBindingId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly OccupantChannelCorrelationToken Token =
        OccupantChannelCorrelationToken.From("opaque.signed.token");

    [Fact]
    public void Request_carries_only_channel_neutral_delivery_material()
    {
        const string renderedMessage = "A new directive is waiting for your response.\nReply using the token below.";

        var request = CreateRequest(renderedMessage);

        Assert.Equal(Organization, request.OrganizationId);
        Assert.Equal(Position, request.PositionId);
        Assert.Equal(Occupant, request.OccupantId);
        Assert.Equal(User, request.UserId);
        Assert.Equal(Binding, request.OccupantChannelBindingId);
        Assert.Equal(Message, request.MessageId);
        Assert.Equal(Thread, request.ThreadId);
        Assert.Equal(renderedMessage, request.RenderedMessage);
        Assert.Equal(Token, request.CorrelationToken);

        var stringProperties = typeof(OccupantChannelDeliveryRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal([nameof(OccupantChannelDeliveryRequest.RenderedMessage)], stringProperties);
    }

    [Fact]
    public void Request_requires_every_identity_rendered_message_and_token()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new OccupantChannelDeliveryRequest(
                null!, Position, Occupant, User, Binding, Message, Thread, "Message", Token));
        Assert.Throws<ArgumentNullException>(() =>
            new OccupantChannelDeliveryRequest(
                Organization, null!, Occupant, User, Binding, Message, Thread, "Message", Token));
        Assert.Throws<ArgumentNullException>(() =>
            new OccupantChannelDeliveryRequest(
                Organization, Position, null!, User, Binding, Message, Thread, "Message", Token));
        Assert.Throws<ArgumentNullException>(() =>
            new OccupantChannelDeliveryRequest(
                Organization, Position, Occupant, null!, Binding, Message, Thread, "Message", Token));
        Assert.Throws<ArgumentNullException>(() =>
            new OccupantChannelDeliveryRequest(
                Organization, Position, Occupant, User, null!, Message, Thread, "Message", Token));
        Assert.Throws<ArgumentNullException>(() =>
            new OccupantChannelDeliveryRequest(
                Organization, Position, Occupant, User, Binding, null!, Thread, "Message", Token));
        Assert.Throws<ArgumentNullException>(() =>
            new OccupantChannelDeliveryRequest(
                Organization, Position, Occupant, User, Binding, Message, null!, "Message", Token));
        Assert.Throws<ArgumentNullException>(() =>
            new OccupantChannelDeliveryRequest(
                Organization, Position, Occupant, User, Binding, Message, Thread, null!, Token));
        Assert.Throws<ArgumentException>(() => CreateRequest(" \t"));
        Assert.Throws<ArgumentNullException>(() =>
            new OccupantChannelDeliveryRequest(
                Organization, Position, Occupant, User, Binding, Message, Thread, "Message", null!));
    }

    [Fact]
    public void Correlation_token_is_opaque_canonical_and_redacted_when_rendered()
    {
        Assert.Equal("opaque.signed.token", Token.Value);
        Assert.Equal("[REDACTED]", Token.ToString());
        Assert.Throws<ArgumentNullException>(() => OccupantChannelCorrelationToken.From(null!));
        Assert.Throws<ArgumentException>(() => OccupantChannelCorrelationToken.From(""));
        Assert.Throws<ArgumentException>(() => OccupantChannelCorrelationToken.From(" token"));
        Assert.Throws<ArgumentException>(() => OccupantChannelCorrelationToken.From("token "));
    }

    [Fact]
    public void Delivery_result_is_either_success_or_a_structured_failure()
    {
        var success = OccupantChannelDeliveryResult.Succeeded();

        Assert.True(success.IsSuccess);
        Assert.False(success.IsFailure);
        Assert.Null(success.Error);

        var error = new OccupantChannelDeliveryError(
            OccupantChannelDeliveryErrorCode.ChannelUnavailable,
            isRetryable: true);
        var failure = OccupantChannelDeliveryResult.Failed(error);

        Assert.False(failure.IsSuccess);
        Assert.True(failure.IsFailure);
        Assert.Same(error, failure.Error);
        Assert.Equal(OccupantChannelDeliveryErrorCode.ChannelUnavailable, failure.Error!.Code);
        Assert.True(failure.Error.IsRetryable);
        Assert.Throws<ArgumentNullException>(() => OccupantChannelDeliveryResult.Failed(null!));
        Assert.Empty(typeof(OccupantChannelDeliveryResult).GetConstructors());
    }

    [Theory]
    [InlineData(OccupantChannelDeliveryErrorCode.BindingUnavailable, "binding-unavailable")]
    [InlineData(OccupantChannelDeliveryErrorCode.BindingRevoked, "binding-revoked")]
    [InlineData(OccupantChannelDeliveryErrorCode.ConfigurationInvalid, "configuration-invalid")]
    [InlineData(OccupantChannelDeliveryErrorCode.AuthenticationFailed, "authentication-failed")]
    [InlineData(OccupantChannelDeliveryErrorCode.RateLimited, "rate-limited")]
    [InlineData(OccupantChannelDeliveryErrorCode.Timeout, "timeout")]
    [InlineData(OccupantChannelDeliveryErrorCode.Canceled, "canceled")]
    [InlineData(OccupantChannelDeliveryErrorCode.ChannelUnavailable, "channel-unavailable")]
    [InlineData(OccupantChannelDeliveryErrorCode.DeliveryRejected, "delivery-rejected")]
    [InlineData(OccupantChannelDeliveryErrorCode.Unknown, "unknown")]
    public void Delivery_errors_have_stable_channel_neutral_codes(
        OccupantChannelDeliveryErrorCode code,
        string wireValue)
    {
        var error = new OccupantChannelDeliveryError(code, isRetryable: false);

        Assert.Equal(code, error.Code);
        Assert.Equal(wireValue, OccupantChannelDeliveryErrorCodeContract.ToWireValue(code));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OccupantChannelDeliveryError((OccupantChannelDeliveryErrorCode)0, false));
    }

    [Fact]
    public async Task Channel_seam_accepts_a_request_and_cancellation_token()
    {
        var expected = OccupantChannelDeliveryResult.Succeeded();
        IOccupantChannel channel = new StubOccupantChannel(expected);
        using var cancellation = new CancellationTokenSource();
        var request = CreateRequest("Message");

        var actual = await channel.DeliverAsync(request, cancellation.Token);

        Assert.Same(expected, actual);
    }

    [Fact]
    public void User_and_binding_identifiers_are_opaque_non_empty_guids()
    {
        Assert.Equal("11111111-1111-1111-1111-111111111111", User.ToString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", Binding.ToString());
        Assert.Throws<ArgumentException>(() => UserId.From(Guid.Empty));
        Assert.Throws<ArgumentException>(() => OccupantChannelBindingId.From(Guid.Empty));
        Assert.NotEqual(UserId.New(), UserId.New());
        Assert.NotEqual(OccupantChannelBindingId.New(), OccupantChannelBindingId.New());
    }

    private static OccupantChannelDeliveryRequest CreateRequest(string renderedMessage) =>
        new(
            Organization,
            Position,
            Occupant,
            User,
            Binding,
            Message,
            Thread,
            renderedMessage,
            Token);

    private sealed class StubOccupantChannel(OccupantChannelDeliveryResult result)
        : IOccupantChannel
    {
        public Task<OccupantChannelDeliveryResult> DeliverAsync(
            OccupantChannelDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.FromResult(result);
        }
    }
}
