using Akka.Actor;
using Hive.Actors.Gateway;
using Hive.Domain.Ai;
using Hive.Domain.Identity;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F1-05-T07: the gateway entity is thin. It delegates exactly once to IAiGateway,
/// returns the response unchanged, converts an unexpected failure into the same sanitized terminal
/// the caller already saw before T07, and honours the wire form of the caller's cancellation.
/// </summary>
public sealed class AiGatewayActorTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("delivery-lead");
    private static readonly ThreadId Thread = ThreadId.From(Guid.NewGuid());
    private static readonly MessageId Message = MessageId.From(Guid.NewGuid());
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Entity_delegates_once_and_returns_the_response_unchanged()
    {
        var gateway = new RecordingGateway();
        using var system = ActorSystem.Create($"ai-gateway-actor-{Guid.NewGuid():N}");
        var entity = system.ActorOf(AiGatewayActor.Props(gateway), "gateway");

        var reply = await entity.Ask<object>(
            new CompleteAiGatewayCall("corr-1", Request()),
            AskTimeout);

        var completed = Assert.IsType<AiGatewayCallCompleted>(reply);
        Assert.Equal("corr-1", completed.CorrelationId);
        Assert.Same(gateway.Response, completed.Response);
        Assert.Equal(1, gateway.Calls);
        Assert.Equal(Message, Assert.Single(gateway.Requests).MessageId);
    }

    [Fact]
    public async Task Entity_returns_a_sanitized_terminal_when_the_gateway_throws()
    {
        using var system = ActorSystem.Create($"ai-gateway-actor-{Guid.NewGuid():N}");
        var entity = system.ActorOf(
            AiGatewayActor.Props(new ThrowingGateway()),
            "gateway");

        var reply = await entity.Ask<object>(
            new CompleteAiGatewayCall("corr-2", Request()),
            AskTimeout);

        var completed = Assert.IsType<AiGatewayCallCompleted>(reply);
        var error = completed.Response.Error!;
        Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, error.Code);
        Assert.True(error.IsRetryable);
        Assert.Equal(
            "AI gateway invocation failed before returning a structured response.",
            error.Message);
        Assert.Null(error.Diagnostics);
    }

    [Fact]
    public async Task Cancel_command_cancels_the_call_in_flight()
    {
        var gateway = new BlockingGateway();
        using var system = ActorSystem.Create($"ai-gateway-actor-{Guid.NewGuid():N}");
        var entity = system.ActorOf(AiGatewayActor.Props(gateway), "gateway");

        var pending = entity.Ask<object>(
            new CompleteAiGatewayCall("corr-3", Request()),
            TimeSpan.FromSeconds(10));
        Assert.True(await gateway.Started.Task.WaitAsync(AskTimeout));

        entity.Tell(new CancelAiGatewayCall("corr-3"));

        var reply = await pending;
        var canceled = Assert.IsType<AiGatewayCallCanceled>(reply);
        Assert.Equal("corr-3", canceled.CorrelationId);
        Assert.True(gateway.ObservedCancellation);
    }

    [Fact]
    public async Task Cancel_of_an_unknown_call_is_ignored()
    {
        var gateway = new RecordingGateway();
        using var system = ActorSystem.Create($"ai-gateway-actor-{Guid.NewGuid():N}");
        var entity = system.ActorOf(AiGatewayActor.Props(gateway), "gateway");

        entity.Tell(new CancelAiGatewayCall("corr-unknown"));
        var reply = await entity.Ask<object>(
            new CompleteAiGatewayCall("corr-4", Request()),
            AskTimeout);

        Assert.IsType<AiGatewayCallCompleted>(reply);
        Assert.Equal(1, gateway.Calls);
    }

    private static AiGatewayRequest Request() =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify the incoming directive.",
            provider: new AiProviderMetadata("openai", "gpt-5.6-luna"));

    private sealed class RecordingGateway : IAiGateway
    {
        private readonly List<AiGatewayRequest> _requests = new();

        public AiGatewayResponse Response { get; } = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "classified",
            AiFinishReason.Stop);

        public int Calls { get; private set; }

        public IReadOnlyList<AiGatewayRequest> Requests => _requests;

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            _requests.Add(request);
            return Task.FromResult(Response);
        }
    }

    private sealed class ThrowingGateway : IAiGateway
    {
        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("provider client exploded");
    }

    private sealed class BlockingGateway : IAiGateway
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ObservedCancellation { get; private set; }

        public async Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }
}
