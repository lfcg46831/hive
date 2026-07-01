using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiDirectiveProcessingStateMachineTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Received_snapshot_preserves_request_identity_and_initial_history()
    {
        var request = Request();

        var snapshot = AiDirectiveProcessingSnapshot.Received(request, At);

        Assert.Equal(request.CorrelationId, snapshot.CorrelationId);
        Assert.Equal(request.ThreadId, snapshot.ThreadId);
        Assert.Equal(request.DirectiveId, snapshot.DirectiveId);
        Assert.Equal(request.MessageId, snapshot.MessageId);
        Assert.Equal(AiDirectiveProcessingStatus.Received, snapshot.Status);
        Assert.False(snapshot.IsTerminal);
        Assert.Null(snapshot.TerminalReason);
        var transition = Assert.Single(snapshot.History);
        Assert.Equal(AiDirectiveProcessingStatus.Received, transition.Status);
        Assert.Equal(At, transition.OccurredAt);
        Assert.Null(transition.Reason);
    }

    [Fact]
    public void AdvanceTo_accepts_ordered_non_terminal_and_result_terminal_transitions()
    {
        var snapshot = AiDirectiveProcessingSnapshot
            .Received(Request(), At)
            .AdvanceTo(AiDirectiveProcessingStatus.ContextAssembled, At.AddSeconds(1))
            .AdvanceTo(AiDirectiveProcessingStatus.GatewayRequested, At.AddSeconds(2))
            .AdvanceTo(AiDirectiveProcessingStatus.ResponseInterpreted, At.AddSeconds(3))
            .AdvanceTo(AiDirectiveProcessingStatus.ResultEmitted, At.AddSeconds(4));

        Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, snapshot.Status);
        Assert.True(snapshot.IsTerminal);
        Assert.Null(snapshot.TerminalReason);
        Assert.Equal(
            new[]
            {
                AiDirectiveProcessingStatus.Received,
                AiDirectiveProcessingStatus.ContextAssembled,
                AiDirectiveProcessingStatus.GatewayRequested,
                AiDirectiveProcessingStatus.ResponseInterpreted,
                AiDirectiveProcessingStatus.ResultEmitted,
            },
            snapshot.History.Select(transition => transition.Status));
    }

    [Fact]
    public void AdvanceTo_preserves_non_terminal_reason_only_in_transition_history()
    {
        var snapshot = AiDirectiveProcessingSnapshot
            .Received(Request(), At)
            .AdvanceTo(
                AiDirectiveProcessingStatus.ContextAssembled,
                At.AddSeconds(1),
                "context skeleton ready");

        Assert.Equal(AiDirectiveProcessingStatus.ContextAssembled, snapshot.Status);
        Assert.False(snapshot.IsTerminal);
        Assert.Null(snapshot.TerminalReason);
        Assert.Equal("context skeleton ready", snapshot.History.Last().Reason);
    }

    [Fact]
    public void AdvanceTo_rejects_out_of_order_transition_and_terminal_reentry()
    {
        var received = AiDirectiveProcessingSnapshot.Received(Request(), At);

        Assert.Throws<InvalidOperationException>(() =>
            received.AdvanceTo(AiDirectiveProcessingStatus.GatewayRequested, At.AddSeconds(1)));

        var failed = received.AdvanceTo(
            AiDirectiveProcessingStatus.Failed,
            At.AddSeconds(2),
            "context assembly failed");

        Assert.Equal(AiDirectiveProcessingStatus.Failed, failed.Status);
        Assert.Equal("context assembly failed", failed.TerminalReason);
        Assert.True(failed.IsTerminal);
        Assert.Throws<InvalidOperationException>(() =>
            failed.AdvanceTo(AiDirectiveProcessingStatus.ContextAssembled, At.AddSeconds(3)));
    }

    [Fact]
    public async Task AiAgentActor_records_context_assembled_snapshot_and_returns_it_by_correlation()
    {
        var request = Request();
        var system = ActorSystem.Create($"ai-agent-state-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new RecordingInvoker())),
                "agent");

            actor.Tell(request);
            var result = await WaitForSnapshotAsync(actor, request.CorrelationId);

            Assert.True(result.Found);
            var snapshot = Assert.IsType<AiDirectiveProcessingSnapshot>(result.Snapshot);
            Assert.Equal(request.CorrelationId, snapshot.CorrelationId);
            Assert.Equal(request.ThreadId, snapshot.ThreadId);
            Assert.Equal(request.DirectiveId, snapshot.DirectiveId);
            Assert.Equal(request.MessageId, snapshot.MessageId);
            Assert.Equal(AiDirectiveProcessingStatus.ContextAssembled, snapshot.Status);
            Assert.Equal(
                [
                    AiDirectiveProcessingStatus.Received,
                    AiDirectiveProcessingStatus.ContextAssembled,
                ],
                snapshot.History.Select(transition => transition.Status).ToArray());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_returns_missing_snapshot_for_unknown_correlation()
    {
        var system = ActorSystem.Create($"ai-agent-state-missing-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    OccupantId.From("agent-7"),
                    new RecordingInvoker())),
                "agent");

            var result = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot("directive:unknown"),
                Timeout());

            Assert.False(result.Found);
            Assert.Equal("directive:unknown", result.CorrelationId);
            Assert.Null(result.Snapshot);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<AiDirectiveProcessingSnapshotQueryResult> WaitForSnapshotAsync(
        IActorRef actor,
        string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(correlationId),
                TimeSpan.FromSeconds(1));
            if (result.Found)
            {
                return result;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("AI directive processing snapshot was not recorded.");
    }

    private static AiDirectiveProcessingRequest Request()
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From("triage-agent"));
        var occupant = OccupantId.From("agent-7");
        var directive = new OrgDirective(
            MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000901")),
            entity.Organization,
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(entity.Position),
            ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000901")),
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000901")),
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(9, "sha256:t03"),
            entity.Organization,
            entity.Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: PositionId.From("delivery-lead"),
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256))),
            new PositionAuthorityRuntimeConfiguration());

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            PositionState.Restore(new PositionSnapshot(At)),
            occupant,
            directive);
    }

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class RecordingInvoker : IAiAgentGatewayInvoker
    {
        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Succeeded(
                    invocation.Request.OrganizationId,
                    invocation.Request.PositionId,
                    invocation.Request.ThreadId,
                    invocation.Request.MessageId,
                    "ok",
                    AiFinishReason.Stop)));
    }
}
