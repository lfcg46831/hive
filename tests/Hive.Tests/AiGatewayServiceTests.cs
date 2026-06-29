using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;

namespace Hive.Tests;

public sealed class AiGatewayServiceTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task CompleteAsync_rejects_null_request_without_calling_provider()
    {
        var provider = new RecordingAiGatewayProvider(SuccessResponse());
        var gateway = new AiGateway(provider);

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await gateway.CompleteAsync(null!));

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public void Constructor_rejects_null_provider()
    {
        Assert.Throws<ArgumentNullException>(() => new AiGateway(null!));
    }

    [Fact]
    public async Task CompleteAsync_delegates_request_and_cancellation_to_provider()
    {
        var request = Request();
        var response = SuccessResponse();
        var provider = new RecordingAiGatewayProvider(response);
        var gateway = new AiGateway(provider);
        using var cancellation = new CancellationTokenSource();

        var result = await gateway.CompleteAsync(request, cancellation.Token);

        Assert.Same(response, result);
        Assert.Equal(1, provider.CallCount);
        Assert.Same(request, provider.Request);
        Assert.Equal(cancellation.Token, provider.CancellationToken);
    }

    private static AiGatewayRequest Request() =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.");

    private static AiGatewayResponse SuccessResponse() =>
        AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The bug is reproducible.",
            AiFinishReason.Stop);

    private sealed class RecordingAiGatewayProvider(AiGatewayResponse response)
        : IAiGatewayProvider
    {
        public int CallCount { get; private set; }

        public AiGatewayRequest? Request { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;

            return Task.FromResult(response);
        }
    }
}
