using Akka.Actor;
using Akka.Configuration;
using Akka.Pattern;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class PositionActorOccupantReplyTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Lead = PositionId.From("delivery-lead");
    private static readonly PositionId Engineer = PositionId.From("engineer");
    private static readonly PositionId Designer = PositionId.From("designer");
    private static readonly UnitId Unit = UnitId.From("delivery");

    [Fact]
    public async Task Directive_reply_is_persisted_and_emitted_as_a_correlated_report()
    {
        var source = new OrgDirective(
            MessageId.From(Guid.Parse("10000000-0000-0000-0000-000000000001")),
            Organization,
            new PositionEndpointRef(Lead),
            new PositionEndpointRef(Engineer),
            ThreadId.From(Guid.Parse("20000000-0000-0000-0000-000000000001")),
            Priority.High,
            1,
            At,
            At.AddHours(2),
            DirectiveId.From(Guid.Parse("30000000-0000-0000-0000-000000000001")),
            parentDirectiveId: null,
            "Investigate the checkout regression",
            "Customer reports failed checkouts.");
        var replyId = MessageId.From(Guid.Parse("40000000-0000-0000-0000-000000000001"));

        var capture = await EmitAsync(
            Engineer,
            source,
            new EmitOccupantReply(
                source.Id,
                replyId,
                OccupantReplyAuthor.HumanUser("person-alice", "web-inbox"),
                "The fix is deployed and verified.",
                ReportKind.Done));

        var report = Assert.IsType<Report>(capture.Result.Message);
        Assert.Equal(replyId, report.Id);
        Assert.Equal(source.DirectiveId, report.AboutDirectiveId);
        Assert.Equal(ReportKind.Done, report.Kind);
        Assert.Equal(source.Thread, report.Thread);
        Assert.Equal(new PositionEndpointRef(Engineer), report.From);
        Assert.Equal(source.From, report.To);
        Assert.Equal("The fix is deployed and verified.", report.Body);
        Assert.Equal(report, capture.RoutedMessage);
        var persisted = Assert.Single(capture.State.OccupantReplies);
        Assert.Equal(source.Id, persisted.SourceMessageId);
        Assert.Equal(OccupantReplyAuthorKind.HumanUser, persisted.Author.Kind);
        Assert.Equal("person-alice", persisted.Author.SubjectId);
        Assert.Equal("web-inbox", persisted.Author.Channel);
        Assert.Equal(report, persisted.Message);
    }

    [Fact]
    public async Task Peer_request_reply_is_emitted_as_a_correlated_peer_response()
    {
        var source = new PeerRequest(
            MessageId.From(Guid.Parse("10000000-0000-0000-0000-000000000002")),
            Organization,
            new PositionEndpointRef(Designer),
            new PositionEndpointRef(Engineer),
            ThreadId.From(Guid.Parse("20000000-0000-0000-0000-000000000002")),
            Priority.Normal,
            1,
            At,
            deadline: null,
            "Can engineering support the launch review?");
        var replyId = MessageId.From(Guid.Parse("40000000-0000-0000-0000-000000000002"));

        var capture = await EmitAsync(
            Engineer,
            source,
            new EmitOccupantReply(
                source.Id,
                replyId,
                OccupantReplyAuthor.ExternalOccupant("remote-agent-7", "https-api"),
                "Yes, an engineer will join."));

        var response = Assert.IsType<PeerResponse>(capture.Result.Message);
        Assert.Equal(source.Id, response.InReplyTo);
        Assert.Equal(source.Thread, response.Thread);
        Assert.Equal(new PositionEndpointRef(Engineer), response.From);
        Assert.Equal(source.From, response.To);
        Assert.Equal("Yes, an engineer will join.", response.Body);
        Assert.Equal(response, capture.RoutedMessage);
        var persisted = Assert.Single(capture.State.OccupantReplies);
        Assert.Equal(OccupantReplyAuthorKind.ExternalOccupant, persisted.Author.Kind);
        Assert.Equal("remote-agent-7", persisted.Author.SubjectId);
        Assert.Equal("https-api", persisted.Author.Channel);
    }

    [Fact]
    public async Task Escalation_reply_is_emitted_as_a_descending_directive_on_the_same_thread()
    {
        var source = new Escalation(
            MessageId.From(Guid.Parse("10000000-0000-0000-0000-000000000003")),
            Organization,
            new PositionEndpointRef(Engineer),
            new PositionEndpointRef(Lead),
            ThreadId.From(Guid.Parse("20000000-0000-0000-0000-000000000003")),
            Priority.Critical,
            1,
            At,
            At.AddHours(1),
            "Production deploy needs a decision",
            "The standard window has elapsed.",
            ["Wait until tomorrow"]);
        var replyId = MessageId.From(Guid.Parse("40000000-0000-0000-0000-000000000003"));
        var directiveId = DirectiveId.From(
            Guid.Parse("50000000-0000-0000-0000-000000000003"));

        var capture = await EmitAsync(
            Lead,
            source,
            new EmitOccupantReply(
                source.Id,
                replyId,
                OccupantReplyAuthor.HumanUser("person-bob", "web-inbox"),
                "Deploy now using the approved emergency window.",
                replyDirectiveId: directiveId));

        var directive = Assert.IsType<OrgDirective>(capture.Result.Message);
        Assert.Equal(directiveId, directive.DirectiveId);
        Assert.Null(directive.ParentDirectiveId);
        Assert.Equal(source.Thread, directive.Thread);
        Assert.Equal(new PositionEndpointRef(Lead), directive.From);
        Assert.Equal(source.From, directive.To);
        Assert.Equal("Deploy now using the approved emergency window.", directive.Objective);
        Assert.Equal(directive, capture.RoutedMessage);
    }

    [Fact]
    public async Task Message_types_outside_the_closed_reply_mapping_are_rejected_without_emission()
    {
        var source = new Memo(
            MessageId.From(Guid.Parse("10000000-0000-0000-0000-000000000004")),
            Organization,
            new PositionEndpointRef(Lead),
            new PositionEndpointRef(Engineer),
            ThreadId.From(Guid.Parse("20000000-0000-0000-0000-000000000004")),
            Priority.Low,
            1,
            At,
            deadline: null,
            "For information only.");

        var capture = await EmitAsync(
            Engineer,
            source,
            new EmitOccupantReply(
                source.Id,
                MessageId.From(Guid.Parse("40000000-0000-0000-0000-000000000004")),
                OccupantReplyAuthor.HumanUser("person-alice", "web-inbox"),
                "Acknowledged."));

        Assert.False(capture.Result.IsAccepted);
        var error = Assert.Single(capture.Result.Errors);
        Assert.Equal("reply-not-supported", error.Code);
        Assert.Null(capture.RoutedMessage);
        Assert.Empty(capture.State.OccupantReplies);
    }

    private static async Task<EmissionCapture> EmitAsync(
        PositionId sourcePosition,
        OrgMessage source,
        EmitOccupantReply command)
    {
        var entity = PositionEntityId.From(Organization, sourcePosition);
        var emitter = new CapturingMessageEmitter();
        var system = CreateActorSystem();
        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity),
                    PositionOccupantFactory.Instance,
                    null,
                    () => At.AddMinutes(1),
                    null,
                    new OccupantReplyMessageValidator(Relations()),
                    emitter)),
                $"position-occupant-reply-{Guid.NewGuid():N}");
            await WaitForReadyAsync(actor);
            actor.Tell(new AcceptMessage(source));
            await WaitForSourceAsync(actor, source.Id);

            var result = await actor.Ask<OccupantReplyEmissionResult>(command, Timeout());
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());
            return new EmissionCapture(result, emitter.Message, state);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static MaterializedOrganizationRelations Relations()
    {
        var builder = OrganizationRelationsSnapshot.CreateBuilder(
            Organization,
            new OrganizationOwnerEndpointRef());
        builder.AddPosition(Lead, Unit);
        builder.AddPosition(Engineer, Unit, Lead);
        builder.AddPosition(Designer, Unit, Lead);
        return new MaterializedOrganizationRelations(builder.Build());
    }

    private static IPositionConfigurationProvider LoadedProvider(PositionEntityId entity) =>
        new StaticConfigurationProvider(
            PositionRuntimeConfigurationLoadResult.Loaded(new PositionRuntimeConfiguration(
                new PositionConfigurationStamp(1, "sha256:occupant-reply-v1"),
                entity.Organization,
                entity.Position,
                new PositionRuntimeDescriptor(
                    Unit,
                    entity.Position == Lead ? null : Lead,
                    name: entity.Position.Value,
                    timezone: "Europe/Lisbon"),
                new OccupantRuntimeConfiguration(OccupantType.Human),
                new PositionAuthorityRuntimeConfiguration([]))));

    private static ActorSystem CreateActorSystem() =>
        ActorSystem.Create(
            $"position-occupant-reply-tests-{Guid.NewGuid():N}",
            ConfigurationFactory.ParseString("""
                akka.persistence.journal.plugin = "akka.persistence.journal.inmem"
                akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.inmem"
                akka.actor {
                  serializers {
                    hive-position-protocol = "Hive.Actors.Serialization.PositionProtocolJsonSerializer, Hive.Actors"
                  }
                  serialization-bindings {
                    "Hive.Domain.Positions.PositionEvent, Hive.Domain" = hive-position-protocol
                    "Hive.Domain.Positions.PositionSnapshot, Hive.Domain" = hive-position-protocol
                  }
                }
                """));

    private static async Task WaitForReadyAsync(IActorRef actor)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await actor.Ask<PositionRuntimeStatus>(
                GetPositionRuntimeStatus.Instance,
                TimeSpan.FromSeconds(1));
            if (status.OperationalState == PositionOperationalState.Ready)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("PositionActor did not become ready.");
    }

    private static async Task WaitForSourceAsync(IActorRef actor, MessageId sourceMessageId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, TimeSpan.FromSeconds(1));
            if (state.ProcessedMessages.Contains(sourceMessageId))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Source message was not persisted.");
    }

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticConfigurationProvider(
        PositionRuntimeConfigurationLoadResult result) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class CapturingMessageEmitter : IPositionMessageEmitter
    {
        public OrgMessage? Message { get; private set; }

        public void Emit(ActorSystem system, OrgMessage message) => Message = message;
    }

    private sealed record EmissionCapture(
        OccupantReplyEmissionResult Result,
        OrgMessage? RoutedMessage,
        PositionState State);
}
