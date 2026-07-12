using Akka.Actor;
using Akka.Configuration;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class RetainedActionAuthorizationIntegrationTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 12, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Gate_escalation_grant_and_verified_resume_consume_the_retained_action()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (action, escalation) = await fixture.RetainEscalatedToolAsync("call-consume");
        var grant = fixture.Grant(action, escalation, action.Fingerprint, At.AddHours(1));

        Assert.True((await fixture.AdmitAsync(grant)).IsValid);
        await fixture.WaitForActionAsync(action.Id, RetainedActionState.Authorized);

        fixture.Actor.Tell(new ResumeRetainedAction(action.Id, Guid.NewGuid()));
        var consumed = await fixture.WaitForActionAsync(action.Id, RetainedActionState.Consumed);

        Assert.Equal(grant, consumed.AuthorizationGrant);
        var execution = Assert.Single(fixture.Executor.Executions);
        Assert.Equal(action.Id, execution.Action.Id);
        Assert.Equal(grant.Id, execution.GrantId);
        Assert.Equal($"retained-action:{action.Id}:grant:{grant.Id}", execution.IdempotencyKey);
    }

    [Fact]
    public async Task Denial_reason_is_returned_on_the_journal_confirmed_lifecycle_signal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (action, escalation) = await fixture.RetainEscalatedToolAsync("call-denied");
        const string reason = "The requested repository is outside the approved scope.";
        var denial = fixture.Denial(action, escalation, reason);

        Assert.True((await fixture.AdmitAsync(denial)).IsValid);
        var denied = await fixture.WaitForActionAsync(action.Id, RetainedActionState.Denied);

        Assert.Equal(reason, denied.AuthorizationDenial?.Reason);
        var signal = Assert.Single(fixture.Projections.Events.OfType<PositionRetainedActionLifecycleChanged>(),
            item => item.Transition is RetainedActionDenied);
        Assert.Equal(reason, signal.Action.AuthorizationDenial?.Reason);
        Assert.Empty(fixture.Executor.Executions);
    }

    [Fact]
    public async Task Expired_grant_is_not_executed_and_publishes_re_escalation_after_persistence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (action, escalation) = await fixture.RetainEscalatedToolAsync("call-expired");
        var grant = fixture.Grant(action, escalation, action.Fingerprint, At.AddMinutes(1));

        Assert.True((await fixture.AdmitAsync(grant)).IsValid);
        await fixture.WaitForActionAsync(action.Id, RetainedActionState.Authorized);
        fixture.Time.AdvanceTo(At.AddMinutes(1));

        fixture.Actor.Tell(new ResumeRetainedAction(action.Id, Guid.NewGuid()));
        var expired = await fixture.WaitForActionAsync(action.Id, RetainedActionState.Expired);

        Assert.Equal(RetainedActionResumeCoordinator.AuthorizationExpiredCode, expired.ReEscalationCode);
        Assert.Empty(fixture.Executor.Executions);
        var signal = Assert.Single(fixture.Projections.Events.OfType<PositionRetainedActionReEscalationReady>());
        Assert.Equal(action.Id, signal.Action.Id);
        Assert.IsType<RetainedActionExpired>(signal.Transition);
    }

    [Fact]
    public async Task Divergent_fingerprint_returns_the_instance_for_new_action_evaluation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (action, escalation) = await fixture.RetainEscalatedToolAsync("call-original");
        var divergent = ActionFingerprint.From($"sha256:{new string('f', 64)}");
        var grant = fixture.Grant(action, escalation, divergent, At.AddHours(1));

        Assert.True((await fixture.AdmitAsync(grant)).IsValid);
        await fixture.WaitForActionAsync(action.Id, RetainedActionState.Authorized);
        fixture.Actor.Tell(new ResumeRetainedAction(action.Id, Guid.NewGuid()));
        var returned = await fixture.WaitForActionAsync(action.Id, RetainedActionState.Retained);

        Assert.Equal(RetainedActionResumeCoordinator.FingerprintMismatchCode, returned.ReEscalationCode);
        Assert.Null(returned.ActiveGrant);
        Assert.Empty(fixture.Executor.Executions);
        var signal = Assert.Single(fixture.Projections.Events.OfType<PositionRetainedActionReEscalationReady>());
        Assert.IsType<RetainedActionReturned>(signal.Transition);

        var reevaluated = await fixture.CreateEscalatedToolAsync("call-changed");
        Assert.NotEqual(action.Fingerprint, reevaluated.Action.Fingerprint);
        Assert.NotEqual(action.Id, reevaluated.Action.Id);
    }

    [Fact]
    public async Task Second_grant_for_the_same_escalation_is_rejected_as_duplicate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (action, escalation) = await fixture.RetainEscalatedToolAsync("call-duplicate");
        var first = fixture.Grant(action, escalation, action.Fingerprint, At.AddHours(1));
        var duplicate = fixture.Grant(action, escalation, action.Fingerprint, At.AddHours(1));

        Assert.True((await fixture.AdmitAsync(first)).IsValid);
        var result = await fixture.ValidateOnlyAsync(duplicate);

        Assert.Equal([AuthorizationValidationCatalog.ResponseDuplicate()], result.Errors);
        var authorized = await fixture.WaitForActionAsync(action.Id, RetainedActionState.Authorized);
        Assert.Equal(first.Id, authorized.ActiveGrant?.Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ActorSystem _system;
        private readonly AuthorizationEscalationStore _escalations;
        private readonly AuditedAuthorizationRoutingValidator _validator;
        private readonly AiAgentActionGate _gate;
        private readonly AiDirectiveExecutionContext _context;

        private Fixture(
            ActorSystem system,
            IActorRef actor,
            AuthorizationEscalationStore escalations,
            AuditedAuthorizationRoutingValidator validator,
            AiAgentActionGate gate,
            AiDirectiveExecutionContext context,
            RecordingExecutor executor,
            RecordingProjectionPublisher projections,
            MutableTimeProvider time)
        {
            _system = system;
            Actor = actor;
            _escalations = escalations;
            _validator = validator;
            _gate = gate;
            _context = context;
            Executor = executor;
            Projections = projections;
            Time = time;
        }

        public IActorRef Actor { get; }
        public RecordingExecutor Executor { get; }
        public RecordingProjectionPublisher Projections { get; }
        public MutableTimeProvider Time { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var entity = PositionEntityId.From(
                OrganizationId.From("acme"),
                PositionId.From($"authorization-integration-{Guid.NewGuid():N}"));
            var (context, configuration) = Context(entity);
            var time = new MutableTimeProvider(At);
            var executor = new RecordingExecutor();
            var projections = new RecordingProjectionPublisher();
            var audit = new RecordingAuditLog();
            var escalations = new AuthorizationEscalationStore();
            var validator = new AuditedAuthorizationRoutingValidator(
                new AuthorizationRoutingValidator(escalations, time),
                audit,
                time);
            var gate = new AiAgentActionGate(
                new ActionDomainCatalog(
                    1,
                    new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
                    []),
                new ActionDomainCatalogBinding(
                    actionContracts: [ActionDomainActionContract.ForTool("jira")]),
                UnexpectedApprovalResolver.Instance,
                audit,
                () => At,
                () => At);
            var coordinator = new RetainedActionResumeCoordinator(
                EscalatingRetainedActionPolicyEvaluator.Instance,
                executor,
                audit,
                time);
            var system = ActorSystem.Create(
                $"authorization-integration-{Guid.NewGuid():N}",
                ConfigurationFactory.ParseString("""
                    akka.persistence.journal.plugin = "akka.persistence.journal.inmem"
                    akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.inmem"
                    """));
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    new LoadedConfigurationProvider(configuration),
                    PositionOccupantFactory.Instance,
                    projections,
                    () => time.GetUtcNow(),
                    coordinator)),
                "position");
            var fixture = new Fixture(
                system, actor, escalations, validator, gate, context, executor, projections, time);
            await fixture.WaitForReadyAsync();
            return fixture;
        }

        public async Task<(PersistedRetainedAction Action, Escalation Escalation)> RetainEscalatedToolAsync(
            string callId)
        {
            var result = await CreateEscalatedToolAsync(callId);
            Actor.Tell(new RetainAction(result.Action));
            await WaitForActionAsync(result.Action.Id, RetainedActionState.Retained);
            _escalations.Add(new AuthorizationEscalationRecord(
                result.Escalation.Id,
                result.Action.OrganizationId,
                result.Action.ThreadId,
                Assert.IsType<PositionEndpointRef>(result.Escalation.From),
                result.Escalation.To,
                result.Action.Id));
            return result;
        }

        public async Task<(PersistedRetainedAction Action, Escalation Escalation)> CreateEscalatedToolAsync(
            string callId)
        {
            var result = await _gate.EvaluateAsync(
                _context,
                AiAgentActionCandidate.ForTool(
                    new AiToolCall(callId, "jira", new Dictionary<string, object?>
                    {
                        ["issue"] = callId,
                    }),
                    ActingUnderDeclaration.Missing()));
            Assert.Equal(AiAgentActionGateOutcome.RetainedForEscalation, result.Outcome);
            var action = AiAgentRetainedActionFactory.Create(result, At).Action;
            var escalation = Assert.IsType<Escalation>(Assert.Single(result.Retention!.GovernanceMessages));
            return (action, escalation);
        }

        public AuthorizationGrant Grant(
            PersistedRetainedAction action,
            Escalation escalation,
            ActionFingerprint fingerprint,
            DateTimeOffset expiresAt) =>
            new(
                MessageId.New(),
                action.OrganizationId,
                escalation.To,
                escalation.From,
                action.ThreadId,
                Priority.High,
                1,
                At,
                null,
                escalation.Id,
                action.Id,
                fingerprint,
                AuthorityKey.From("delivery.bug-triage"),
                expiresAt,
                null);

        public AuthorizationDenial Denial(
            PersistedRetainedAction action,
            Escalation escalation,
            string reason) =>
            new(
                MessageId.New(),
                action.OrganizationId,
                escalation.To,
                escalation.From,
                action.ThreadId,
                Priority.High,
                1,
                At,
                null,
                escalation.Id,
                action.Id,
                reason);

        public async Task<ValidationResult> AdmitAsync(AuthorizationGrant grant)
        {
            var result = await _validator.ValidateAsync(grant);
            if (result.IsValid)
            {
                _escalations.Resolve(grant.InReplyTo, grant.Id);
                Actor.Tell(new AuthorizeRetainedAction(grant));
            }

            return result;
        }

        public async Task<ValidationResult> AdmitAsync(AuthorizationDenial denial)
        {
            var result = await _validator.ValidateAsync(denial);
            if (result.IsValid)
            {
                _escalations.Resolve(denial.InReplyTo, denial.Id);
                Actor.Tell(new DenyRetainedAction(denial));
            }

            return result;
        }

        public ValueTask<ValidationResult> ValidateOnlyAsync(AuthorizationGrant grant) =>
            _validator.ValidateAsync(grant);

        public async Task<PersistedRetainedAction> WaitForActionAsync(
            RetainedActionId actionId,
            RetainedActionState state)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var current = await Actor.Ask<PositionState>(
                        GetPositionState.Instance,
                        TimeSpan.FromSeconds(1));
                    if (current.RetainedActions.TryGetValue(actionId, out var action)
                        && action.State == state)
                    {
                        return action;
                    }
                }
                catch (AskTimeoutException)
                {
                }

                await Task.Delay(25);
            }

            throw new TimeoutException($"Retained action '{actionId}' did not reach '{state}'.");
        }

        public async ValueTask DisposeAsync()
        {
            await _system.Terminate();
        }

        private async Task WaitForReadyAsync()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var status = await Actor.Ask<PositionRuntimeStatus>(
                        GetPositionRuntimeStatus.Instance,
                        TimeSpan.FromSeconds(1));
                    if (status.OperationalState == PositionOperationalState.Ready)
                    {
                        return;
                    }
                }
                catch (AskTimeoutException)
                {
                }

                await Task.Delay(25);
            }

            throw new TimeoutException("Position actor did not become ready.");
        }

        private static (AiDirectiveExecutionContext Context, PositionRuntimeConfiguration Configuration)
            Context(PositionEntityId entity)
        {
            var superior = PositionId.From("delivery-lead");
            var directive = new OrgDirective(
                MessageId.New(),
                entity.Organization,
                new PositionEndpointRef(superior),
                new PositionEndpointRef(entity.Position),
                ThreadId.New(),
                Priority.High,
                1,
                At,
                At.AddHours(2),
                DirectiveId.New(),
                null,
                "Triage a customer incident.",
                "A customer reports a production regression.");
            var configuration = new PositionRuntimeConfiguration(
                new PositionConfigurationStamp(1, "sha256:authorization-integration"),
                entity.Organization,
                entity.Position,
                new PositionRuntimeDescriptor(UnitId.From("delivery"), superior),
                new OccupantRuntimeConfiguration(OccupantType.AiAgent),
                new PositionAuthorityRuntimeConfiguration(),
                []);
            var request = AiDirectiveProcessingRequest.Create(
                entity,
                configuration,
                PositionState.Empty,
                OccupantId.From("integration-agent"),
                directive);
            return (AiDirectiveExecutionContext.From(request), configuration);
        }
    }

    private sealed class AuthorizationEscalationStore : IAuthorizationEscalationLog
    {
        private readonly Dictionary<MessageId, AuthorizationEscalationRecord> _records = [];

        public void Add(AuthorizationEscalationRecord record) => _records.Add(record.EscalationId, record);

        public void Resolve(MessageId escalationId, MessageId resolutionId)
        {
            var current = _records[escalationId];
            _records[escalationId] = new AuthorizationEscalationRecord(
                current.EscalationId,
                current.OrganizationId,
                current.Thread,
                current.Requester,
                current.Recipient,
                current.RetainedActionId,
                resolutionId);
        }

        public ValueTask<AuthorizationEscalationRecord?> FindEscalationAsync(
            OrganizationId organizationId,
            MessageId escalationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _records.TryGetValue(escalationId, out var record);
            return ValueTask.FromResult(record?.OrganizationId == organizationId ? record : null);
        }
    }

    private sealed class LoadedConfigurationProvider(PositionRuntimeConfiguration configuration)
        : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PositionRuntimeConfigurationLoadResult.Loaded(configuration));
    }

    private sealed class UnexpectedApprovalResolver : IAiActionApprovalResolver
    {
        public static UnexpectedApprovalResolver Instance { get; } = new();

        public ValueTask<AiActionApprovalResolution> ResolveAsync(
            AiActionApprovalResolutionQuery query,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Approval resolution was not expected.");
    }

    private sealed class RecordingExecutor : IRetainedActionExecutor
    {
        public List<RetainedActionExecutionRequest> Executions { get; } = [];

        public ValueTask<RetainedActionExecutionResult> ExecuteAsync(
            RetainedActionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Executions.Add(request);
            return ValueTask.FromResult(RetainedActionExecutionResult.Success());
        }
    }

    private sealed class RecordingProjectionPublisher : IPositionProjectionPublisher
    {
        public List<PositionProjectionEvent> Events { get; } = [];

        public void Publish(PositionProjectionEvent @event) => Events.Add(@event);
    }

    private sealed class RecordingAuditLog : IJourneyAuditLog
    {
        private readonly List<JourneyAuditRecord> _records = [];

        public void Append(JourneyAuditRecord record) => _records.Add(record);

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) =>
            _records.Where(record => record.ThreadId == threadId
                                     && (directiveId is null || record.DirectiveId == directiveId))
                .ToArray();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void AdvanceTo(DateTimeOffset now) => _now = now;
    }
}
