using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Tests;

public sealed class PositionOccupantFactoryTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("bug-triage");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000801"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000801"));

    [Fact]
    public async Task Create_ai_agent_wires_configured_gateway_invoker()
    {
        var invoker = new RecordingInvoker();
        var factory = new PositionOccupantFactory(invoker);
        var invocation = new AiAgentGatewayInvocation("factory:ai-agent", Request());
        var system = ActorSystem.Create($"position-occupant-factory-ai-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                factory.Create(OccupantId.From("agent-7"), OccupantType.AiAgent),
                "agent");

            var result = await actor.Ask<AiAgentGatewayInvocationResult>(
                invocation,
                Timeout());

            Assert.True(result.IsSuccess);
            Assert.Equal("factory:ai-agent", result.CorrelationId);
            Assert.Equal(1, invoker.CallCount);
            Assert.Same(invocation, invoker.Invocation);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public void Create_rejects_null_occupant_and_unknown_occupant_type()
    {
        var factory = new PositionOccupantFactory();

        Assert.Throws<ArgumentNullException>(() => factory.Create(null!, OccupantType.AiAgent));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            factory.Create(OccupantId.From("agent-7"), (OccupantType)42));
    }

    private static AiGatewayRequest Request() =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify the incoming directive.");

    private static AiGatewayResponse SuccessResponse() =>
        AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The directive is actionable.",
            AiFinishReason.Stop);

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class RecordingInvoker : IAiAgentGatewayInvoker
    {
        public int CallCount { get; private set; }

        public AiAgentGatewayInvocation? Invocation { get; private set; }

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Invocation = invocation;

            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                SuccessResponse()));
        }
    }
}
