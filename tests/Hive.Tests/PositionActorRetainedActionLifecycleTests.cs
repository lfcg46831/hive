using Akka.Actor;
using Akka.Configuration;
using Hive.Actors.Positions;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class PositionActorRetainedActionLifecycleTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 12, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Position_actor_persists_authorization_recovers_and_expires_replacement_grant()
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From($"retained-lifecycle-{Guid.NewGuid():N}"));
        var system = ActorSystem.Create(
            $"retained-lifecycle-{Guid.NewGuid():N}",
            ConfigurationFactory.ParseString("""
                akka.persistence.journal.plugin = "akka.persistence.journal.inmem"
                akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.inmem"
                """));

        try
        {
            var provider = new LoadedConfigurationProvider(entity);
            var action = Action(entity);
            var firstGrant = Grant(action);
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(entity.Value, provider, () => At)),
                "position");

            actor.Tell(new RetainAction(action));
            actor.Tell(new AuthorizeRetainedAction(firstGrant));
            actor.Tell(new DenyRetainedAction(Denial(action)));

            var authorized = await WaitForStateAsync(
                actor,
                state => state.RetainedActions.TryGetValue(action.Id, out var current)
                    && current.State == RetainedActionState.Authorized);
            Assert.Equal(firstGrant, authorized.RetainedActions[action.Id].ActiveGrant);

            await actor.GracefulStop(TimeSpan.FromSeconds(5));
            var restarted = system.ActorOf(
                Props.Create(() => new PositionActor(entity.Value, provider, () => At.AddMinutes(1))),
                "position-restarted");
            var recovered = await WaitForStateAsync(
                restarted,
                state => state.RetainedActions.TryGetValue(action.Id, out var current)
                    && current.State == RetainedActionState.Authorized);
            Assert.Equal(firstGrant, recovered.RetainedActions[action.Id].ActiveGrant);

            restarted.Tell(new ReturnRetainedAction(action.Id, firstGrant.Id, "policy-tightened"));
            var returned = await WaitForStateAsync(
                restarted,
                state => state.RetainedActions.TryGetValue(action.Id, out var current)
                    && current.State == RetainedActionState.Retained
                    && current.ReEscalationCode == "policy-tightened");
            Assert.Null(returned.RetainedActions[action.Id].ActiveGrant);

            var replacement = Grant(action);
            restarted.Tell(new AuthorizeRetainedAction(replacement));
            restarted.Tell(new ExpireRetainedAction(
                action.Id,
                replacement.Id,
                "authorization-expired"));
            var expired = await WaitForStateAsync(
                restarted,
                state => state.RetainedActions.TryGetValue(action.Id, out var current)
                    && current.State == RetainedActionState.Expired);

            Assert.Equal(replacement, expired.RetainedActions[action.Id].AuthorizationGrant);
            Assert.Null(expired.RetainedActions[action.Id].ActiveGrant);
            Assert.Equal("authorization-expired", expired.RetainedActions[action.Id].ReEscalationCode);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<PositionState> WaitForStateAsync(
        IActorRef actor,
        Func<PositionState, bool> predicate)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(10);
        PositionState? latest = null;
        while (DateTime.UtcNow < timeoutAt)
        {
            latest = await actor.Ask<PositionState>(GetPositionState.Instance, TimeSpan.FromSeconds(2));
            if (predicate(latest))
            {
                return latest;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"PositionActor lifecycle state did not converge. Last state: {latest}.");
    }

    private static PersistedRetainedAction Action(PositionEntityId entity) =>
        new(
            RetainedActionId.New(),
            ActionFingerprint.From("sha256:0000000000000000000000000000000000000000000000000000000000000013"),
            RetainedActionKind.Tool,
            "github.create-issue",
            "{\"arguments\":{\"title\":\"Incident\"}}",
            "{\"repository\":\"acme/hive\"}",
            "directive:actor-lifecycle",
            entity.Organization,
            entity.Position,
            ThreadId.New(),
            MessageId.New(),
            DirectiveId.New(),
            null,
            "action-gate-escalation-required",
            At);

    private static AuthorizationGrant Grant(PersistedRetainedAction action) =>
        new(
            MessageId.New(),
            action.OrganizationId,
            new OrganizationOwnerEndpointRef(),
            new PositionEndpointRef(action.PositionId),
            action.ThreadId,
            Priority.High,
            1,
            At,
            null,
            MessageId.New(),
            action.Id,
            action.Fingerprint,
            AuthorityKey.From("governance.authorize-retained-action"),
            At.AddHours(1),
            null);

    private static AuthorizationDenial Denial(PersistedRetainedAction action) =>
        new(
            MessageId.New(),
            action.OrganizationId,
            new OrganizationOwnerEndpointRef(),
            new PositionEndpointRef(action.PositionId),
            action.ThreadId,
            Priority.High,
            1,
            At,
            null,
            MessageId.New(),
            action.Id,
            "Denied after authorization.");

    private sealed class LoadedConfigurationProvider(PositionEntityId expected) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expected, entityId);
            return Task.FromResult(PositionRuntimeConfigurationLoadResult.Loaded(
                new PositionRuntimeConfiguration(
                    new PositionConfigurationStamp(1, "sha256:lifecycle"),
                    entityId.Organization,
                    entityId.Position,
                    new PositionRuntimeDescriptor(UnitId.From("delivery")),
                    new OccupantRuntimeConfiguration(OccupantType.Human),
                    new PositionAuthorityRuntimeConfiguration(),
                    [])));
        }
    }
}
