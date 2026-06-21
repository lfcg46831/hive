using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class HorizontalMessageTests
{
    private static readonly DateTimeOffset SentAt =
        new(2026, 6, 21, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Memo_preserves_payload_and_derives_horizontal_channel()
    {
        var message = new Memo(
            MessageId.New(), OrganizationId.From("acme"), Position("developer"),
            Position("tester"), ThreadId.New(), Priority.Normal, 1, SentAt, null,
            "The candidate build is ready for verification");

        Assert.Equal("The candidate build is ready for verification", message.Body);
        Assert.Equal(MessageChannel.Horizontal, message.Channel);
    }

    [Fact]
    public void PeerRequest_preserves_payload_and_derives_horizontal_channel()
    {
        var message = new PeerRequest(
            MessageId.New(), OrganizationId.From("acme"), Position("developer"),
            Position("tester"), ThreadId.New(), Priority.High, 1, SentAt, null,
            "Can you verify the production reproduction steps?");

        Assert.Equal("Can you verify the production reproduction steps?", message.Ask);
        Assert.Equal(MessageChannel.Horizontal, message.Channel);
    }

    [Fact]
    public void PeerResponse_preserves_payload_and_derives_horizontal_channel()
    {
        var requestId = MessageId.New();

        var message = new PeerResponse(
            MessageId.New(), OrganizationId.From("acme"), Position("tester"),
            Position("developer"), ThreadId.New(), Priority.High, 1, SentAt, null,
            requestId, "The issue reproduces on the production configuration");

        Assert.Equal(requestId, message.InReplyTo);
        Assert.Equal("The issue reproduces on the production configuration", message.Body);
        Assert.Equal(MessageChannel.Horizontal, message.Channel);
    }

    [Fact]
    public void PeerResponse_rejects_missing_request_reference()
    {
        Assert.Throws<ArgumentNullException>(() => new PeerResponse(
            MessageId.New(), OrganizationId.From("acme"), Position("tester"),
            Position("developer"), ThreadId.New(), Priority.High, 1, SentAt, null,
            null!, "Response"));
    }

    [Fact]
    public void Horizontal_payload_properties_are_get_only()
    {
        AssertGetOnly<Memo>(nameof(Memo.Body), nameof(Memo.Channel));
        AssertGetOnly<PeerRequest>(nameof(PeerRequest.Ask), nameof(PeerRequest.Channel));
        AssertGetOnly<PeerResponse>(
            nameof(PeerResponse.InReplyTo),
            nameof(PeerResponse.Body),
            nameof(PeerResponse.Channel));
    }

    private static PositionEndpointRef Position(string value) =>
        new(PositionId.From(value));

    private static void AssertGetOnly<T>(params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = typeof(T).GetProperty(propertyName);

            Assert.NotNull(property);
            Assert.Null(property.SetMethod);
        }
    }
}
