using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using Akka.Cluster.Sharding;
using Akka.Persistence;
using Akka.Pattern;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

/// <summary>
/// Persistent entity for one sharded position (US-F0-06-T06b). Recovery restores the latest
/// snapshot and folds subsequent persisted events into <see cref="PositionState"/> before Akka
/// releases commands from the recovery stash.
/// </summary>
internal sealed class PositionActor :
    ReceivePersistentActor,
    IWithUnboundedStash
{
    internal const string PersistenceIdPrefix = "position:";

    private readonly IPositionConfigurationProvider _configurationProvider;
    private readonly IPositionOccupantFactory _occupantFactory;
    private readonly IPositionProjectionPublisher? _projectionPublisher;
    private readonly RetainedActionResumeCoordinator _resumeCoordinator;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<PositionOccupantKey, IActorRef> _occupantActors = new();
    private readonly HashSet<Guid> _handledResumeAttempts = [];
    private readonly HashSet<RetainedActionId> _resumingActions = [];

    private PositionState _state = PositionState.Empty;
    private PositionOperationalState _operationalState = PositionOperationalState.Recovering;
    private PositionConfigurationBlockReason? _configurationBlockReason;
    private PositionRuntimeConfiguration? _runtimeConfiguration;
    private bool _passivationRequested;

    public PositionActor(string entityId)
        : this(
            entityId,
            new UnavailableConfigurationProvider(
                "No position configuration provider was supplied to the PositionActor."),
            PositionOccupantFactory.Instance,
            projectionPublisher: null,
            () => DateTimeOffset.UtcNow)
    {
    }

    public PositionActor(string entityId, IPositionConfigurationProvider configurationProvider)
        : this(
            entityId,
            configurationProvider,
            PositionOccupantFactory.Instance,
            projectionPublisher: null,
            () => DateTimeOffset.UtcNow)
    {
    }

    public PositionActor(
        string entityId,
        IPositionConfigurationProvider configurationProvider,
        Func<DateTimeOffset> clock)
        : this(entityId, configurationProvider, PositionOccupantFactory.Instance, projectionPublisher: null, clock)
    {
    }

    public PositionActor(
        string entityId,
        IPositionConfigurationProvider configurationProvider,
        IPositionProjectionPublisher projectionPublisher,
        Func<DateTimeOffset> clock)
        : this(entityId, configurationProvider, PositionOccupantFactory.Instance, projectionPublisher, clock)
    {
    }

    public PositionActor(
        string entityId,
        IPositionConfigurationProvider configurationProvider,
        IPositionOccupantFactory occupantFactory,
        Func<DateTimeOffset> clock)
        : this(entityId, configurationProvider, occupantFactory, projectionPublisher: null, clock)
    {
    }

    public PositionActor(
        string entityId,
        IPositionConfigurationProvider configurationProvider,
        IPositionOccupantFactory occupantFactory,
        IPositionProjectionPublisher? projectionPublisher,
        Func<DateTimeOffset> clock)
        : this(
            entityId,
            configurationProvider,
            occupantFactory,
            projectionPublisher,
            clock,
            resumeCoordinator: null)
    {
    }

    public PositionActor(
        string entityId,
        IPositionConfigurationProvider configurationProvider,
        IPositionOccupantFactory occupantFactory,
        IPositionProjectionPublisher? projectionPublisher,
        Func<DateTimeOffset> clock,
        RetainedActionResumeCoordinator? resumeCoordinator)
    {
        EntityId = PositionEntityId.Parse(entityId);
        _configurationProvider = configurationProvider
            ?? throw new ArgumentNullException(nameof(configurationProvider));
        _occupantFactory = occupantFactory
            ?? throw new ArgumentNullException(nameof(occupantFactory));
        _projectionPublisher = projectionPublisher;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _resumeCoordinator = resumeCoordinator ?? new RetainedActionResumeCoordinator(
            EscalatingRetainedActionPolicyEvaluator.Instance,
            UnavailableRetainedActionExecutor.Instance,
            Domain.Auditing.NoopJourneyAuditLog.Instance);
        PersistenceId = PersistenceIdFor(EntityId.Value);

        Recover<SnapshotOffer>(RecoverSnapshot);
        Recover<PositionEvent>(ApplyRecovered);
        Recover<RecoveryCompleted>(_ => BeginConfigurationLoad());

        Command<GetPositionState>(_ => Sender.Tell(_state));
        Command<GetPositionRuntimeStatus>(_ => Sender.Tell(RuntimeStatus()));
        Command<PositionConfigurationLoadCompleted>(HandleConfigurationLoadCompleted);
        Command<PositionConfigurationLoadFailed>(failure => throw new PositionConfigurationGateException(
            PersistenceId,
            failure.Cause));
        // Neutral end-of-processing signal from an occupant child. The position records only
        // delivery completion so recovery can distinguish in-flight work from completed work.
        Command<PositionOccupantProcessingCompleted>(completed =>
            WhenReady(() =>
                PersistProcessingCompletion(completed)));
        Command<AcceptMessage>(command =>
        {
            WhenReady(() =>
            {
                if (_state.ProcessedMessages.Contains(command.Message.Id))
                {
                    PublishProjection(new PositionMessageDuplicateRejected(
                        EntityId,
                        command.Message.Id,
                        command.Message.Thread,
                        _clock()));
                    return;
                }

                PersistAcceptedMessage(command.Message);
            });
        });
        Command<OpenTask>(command =>
            WhenReady(() =>
                PersistAndApply(new TaskCreated(
                    command.TaskId,
                    command.Thread,
                    command.Title,
                    command.Priority,
                    _clock(),
                    command.Deadline,
                    command.CausedBy))));
        Command<UpdateTask>(command =>
            WhenReady(() =>
                PersistAndApply(new TaskUpdated(
                    command.TaskId,
                    command.Note,
                    _clock(),
                    command.Priority,
                    command.Deadline))));
        Command<CompleteTask>(command =>
            WhenReady(() =>
                PersistAndApply(new TaskCompleted(command.TaskId, _clock(), command.Summary))));
        Command<UpdateShortMemory>(command =>
            WhenReady(() =>
                PersistAndApply(new ShortMemoryUpdated(command.Key, command.Value, _clock()))));
        Command<ChangeOccupant>(command =>
            WhenReady(() =>
                PersistOccupantChange(command)));
        Command<RetainAction>(command =>
            WhenReady(() =>
                PersistRetainedAction(command)));
        Command<AuthorizeRetainedAction>(command =>
            WhenReady(() =>
                PersistAuthorization(command)));
        Command<DenyRetainedAction>(command =>
            WhenReady(() =>
                PersistDenial(command)));
        Command<ConsumeRetainedAction>(command =>
            WhenReady(() =>
                PersistAuthorizedTransition(
                    command.ActionId,
                    command.GrantId,
                    () => new RetainedActionConsumed(command.ActionId, command.GrantId, _clock()))));
        Command<ExpireRetainedAction>(command =>
            WhenReady(() =>
                PersistAuthorizedTransition(
                    command.ActionId,
                    command.GrantId,
                    () => new RetainedActionExpired(
                        command.ActionId,
                        command.GrantId,
                        command.ReEscalationCode,
                        _clock()))));
        Command<ReturnRetainedAction>(command =>
            WhenReady(() =>
                PersistAuthorizedTransition(
                    command.ActionId,
                    command.GrantId,
                    () => new RetainedActionReturned(
                        command.ActionId,
                        command.GrantId,
                        command.ReEscalationCode,
                        _clock()))));
        Command<ResumeRetainedAction>(command =>
            WhenReady(() => BeginRetainedActionResume(command)));
        Command<RetainedActionResumeCompleted>(HandleRetainedActionResumeCompleted);
        Command<RetainedActionResumeFailed>(failed =>
            _resumingActions.Remove(failed.ActionId));
        Command<RequestPassivation>(command =>
            WhenReady(() =>
                PersistPassivationIfAllowed(command)));
        Command<PositionPassivationStop>(_ =>
        {
            if (_passivationRequested)
            {
                Context.Stop(Self);
            }
        });
    }

    public override string PersistenceId { get; }

    public new IStash Stash { get; set; } = null!;

    internal PositionEntityId EntityId { get; }

    internal static string PersistenceIdFor(string entityId)
    {
        var parsed = PositionEntityId.Parse(entityId);
        return $"{PersistenceIdPrefix}{parsed.Value}";
    }

    private void RecoverSnapshot(SnapshotOffer offer)
    {
        if (offer.Snapshot is not PositionSnapshot snapshot)
        {
            throw new InvalidOperationException(
                $"PositionActor snapshot for '{PersistenceId}' must be a {nameof(PositionSnapshot)}.");
        }

        _state = PositionState.Restore(snapshot);
    }

    private void ApplyRecovered(PositionEvent @event) => _state = _state.Apply(@event);

    private void PersistAndApply(PositionEvent @event) =>
        Persist(@event, ApplyPersisted);

    private void PersistAcceptedMessage(OrgMessage message)
    {
        var events = new List<PositionEvent>
        {
            new MessageReceived(message, _clock()),
        };

        if (_state.Occupant is { } occupant && _state.OccupantType is { } occupantType)
        {
            events.Add(new MessageDispatched(
                message.Id,
                message.Thread,
                occupant,
                occupantType,
                _clock()));
        }

        PersistEvents(events);
    }

    private void PersistOccupantChange(ChangeOccupant command)
    {
        var events = new List<PositionEvent>
        {
            new OccupantChanged(command.Occupant, command.Type, _clock()),
        };

        foreach (var message in _state.Inbox)
        {
            events.Add(new MessageDispatched(
                message.Id,
                message.Thread,
                command.Occupant,
                command.Type,
                _clock()));
        }

        PersistEvents(events);
    }

    private void PersistProcessingCompletion(PositionOccupantProcessingCompleted completed)
    {
        if (!_state.Inbox.Any(message => message.Id == completed.MessageId))
        {
            return;
        }

        PersistAndApply(new MessageProcessingCompleted(
            completed.CorrelationId,
            completed.MessageId,
            completed.ThreadId,
            CompletionStatus(completed.Status),
            _clock(),
            completed.FailureCode));
    }

    private void PersistRetainedAction(RetainAction command)
    {
        if (_state.RetainedActions.ContainsKey(command.Action.Id)
            || _state.RetainedActions.Values.Any(action => string.Equals(
                action.CorrelationId,
                command.Action.CorrelationId,
                StringComparison.Ordinal)))
        {
            return;
        }

        if (command.Action.OrganizationId != EntityId.Organization
            || command.Action.PositionId != EntityId.Position)
        {
            throw new ArgumentException(
                "Retained action organization and position must match the target entity.",
                nameof(command));
        }

        PersistAndApply(new ActionRetained(command.Action));
    }

    private void PersistAuthorization(AuthorizeRetainedAction command)
    {
        var grant = command.Grant;
        if (!_state.RetainedActions.TryGetValue(grant.RetainedActionId, out var action)
            || action.State != RetainedActionState.Retained
            || action.AuthorizationGrant?.Id == grant.Id
            || !TargetsAction(grant, action))
        {
            return;
        }

        PersistAndApply(new RetainedActionAuthorized(grant, _clock()));
    }

    private void PersistDenial(DenyRetainedAction command)
    {
        var denial = command.Denial;
        if (!_state.RetainedActions.TryGetValue(denial.RetainedActionId, out var action)
            || action.State != RetainedActionState.Retained
            || !TargetsAction(denial, action))
        {
            return;
        }

        PersistAndApply(new RetainedActionDenied(denial, _clock()));
    }

    private void PersistAuthorizedTransition(
        RetainedActionId actionId,
        MessageId grantId,
        Func<PositionEvent> createEvent)
    {
        if (!_state.RetainedActions.TryGetValue(actionId, out var action)
            || action.State != RetainedActionState.Authorized
            || action.ActiveGrant?.Id != grantId)
        {
            return;
        }

        PersistAndApply(createEvent());
    }

    private void BeginRetainedActionResume(ResumeRetainedAction command)
    {
        if (!_handledResumeAttempts.Add(command.AttemptId)
            || !_state.RetainedActions.TryGetValue(command.ActionId, out var action)
            || !_resumingActions.Add(command.ActionId))
        {
            return;
        }

        var runtimeConfiguration = _runtimeConfiguration
            ?? throw new InvalidOperationException(
                $"PositionActor '{PersistenceId}' cannot resume an action before runtime configuration is loaded.");

        _resumeCoordinator
            .ResumeAsync(action, runtimeConfiguration, command.AttemptId)
            .AsTask()
            .PipeTo(
                Self,
                success: result => new RetainedActionResumeCompleted(
                    command.ActionId,
                    command.AttemptId,
                    result),
                failure: exception => new RetainedActionResumeFailed(
                    command.ActionId,
                    command.AttemptId,
                    exception));
    }

    private void HandleRetainedActionResumeCompleted(RetainedActionResumeCompleted completed)
    {
        if (!_state.RetainedActions.TryGetValue(completed.ActionId, out var current)
            || current.State != RetainedActionState.Authorized
            || current.ActiveGrant?.Id != completed.Result.GrantId)
        {
            _resumingActions.Remove(completed.ActionId);
            return;
        }

        var occurredAt = _clock();
        var activeGrantId = current.ActiveGrant!.Id;
        PositionEvent? transition = completed.Result.Outcome switch
        {
            RetainedActionResumeOutcome.Consumed =>
                new RetainedActionConsumed(current.Id, activeGrantId, occurredAt),
            RetainedActionResumeOutcome.Expired =>
                new RetainedActionExpired(current.Id, activeGrantId, completed.Result.Code, occurredAt),
            RetainedActionResumeOutcome.Returned =>
                new RetainedActionReturned(current.Id, activeGrantId, completed.Result.Code, occurredAt),
            _ => null,
        };

        if (transition is not null)
        {
            Persist(transition, persisted =>
            {
                ApplyPersisted(persisted);
                _resumingActions.Remove(completed.ActionId);
            });
            return;
        }

        _resumingActions.Remove(completed.ActionId);
    }

    private bool TargetsAction(OrgMessage resolution, PersistedRetainedAction action) =>
        resolution.OrganizationId == EntityId.Organization
        && resolution.OrganizationId == action.OrganizationId
        && resolution.Thread == action.ThreadId
        && resolution.To is PositionEndpointRef destination
        && destination.PositionId == EntityId.Position
        && destination.PositionId == action.PositionId;

    private void PersistPendingDispatches(Action? afterDispatch = null)
    {
        if (_state.Occupant is not { } occupant || _state.OccupantType is not { } occupantType)
        {
            afterDispatch?.Invoke();
            return;
        }

        var alreadyDispatched = _state.Inbox
            .Where(message => _state.RecentHistory.Contains(message.Id))
            .ToArray();
        var events = _state.Inbox
            .Where(message => !_state.RecentHistory.Contains(message.Id))
            .Select(message => (PositionEvent)new MessageDispatched(
                message.Id,
                message.Thread,
                occupant,
                occupantType,
                _clock()))
            .ToArray();

        PersistEvents(events, () =>
        {
            foreach (var message in alreadyDispatched)
            {
                DeliverToOccupant(message, occupant, occupantType);
            }

            afterDispatch?.Invoke();
        });
    }

    private void PersistEvents(IReadOnlyList<PositionEvent> events, Action? afterLast = null)
    {
        if (events.Count == 0)
        {
            afterLast?.Invoke();
            return;
        }

        var remaining = events.Count;
        PersistAll(events, persisted =>
        {
            ApplyPersisted(persisted);
            remaining--;
            if (remaining == 0)
            {
                afterLast?.Invoke();
            }
        });
    }

    private void ApplyPersisted(PositionEvent persisted)
    {
        var previousOccupantKey = CurrentOccupantKey();
        OrgMessage? dispatchedMessage = null;
        if (persisted is MessageDispatched dispatched)
        {
            dispatchedMessage = _state.Inbox.FirstOrDefault(message => message.Id == dispatched.Message);
        }

        _state = _state.Apply(persisted);
        PublishProjection(new PositionEventCommitted(EntityId, persisted));

        if (persisted is ActionRetained retained)
        {
            PublishProjection(new PositionRetainedActionReady(EntityId, retained.Action));
        }

        if (RetainedActionIdFor(persisted) is { } retainedActionId
            && _state.RetainedActions.TryGetValue(retainedActionId, out var retainedAction))
        {
            PublishProjection(new PositionRetainedActionLifecycleChanged(
                EntityId,
                retainedAction,
                persisted));

            if (persisted is RetainedActionExpired or RetainedActionReturned)
            {
                PublishProjection(new PositionRetainedActionReEscalationReady(
                    EntityId,
                    retainedAction,
                    persisted));
            }
        }

        if (persisted is OccupantChanged changed)
        {
            StopObsoleteOccupant(
                previousOccupantKey,
                new PositionOccupantKey(changed.Occupant, changed.Type));
        }

        if (persisted is MessageDispatched dispatchEvent && dispatchedMessage is not null)
        {
            DeliverToOccupant(
                dispatchedMessage,
                dispatchEvent.Occupant,
                dispatchEvent.OccupantType);
        }
    }

    private static RetainedActionId? RetainedActionIdFor(PositionEvent @event) =>
        @event switch
        {
            RetainedActionAuthorized authorized => authorized.Grant.RetainedActionId,
            RetainedActionDenied denied => denied.Denial.RetainedActionId,
            RetainedActionConsumed consumed => consumed.ActionId,
            RetainedActionExpired expired => expired.ActionId,
            RetainedActionReturned returned => returned.ActionId,
            _ => null,
        };

    private void DeliverToOccupant(
        OrgMessage message,
        OccupantId occupant,
        OccupantType occupantType) =>
        ResolveOccupant(occupant, occupantType)
            .Tell(CreateOccupantPayload(occupant, occupantType, message));

    private object CreateOccupantPayload(
        OccupantId occupant,
        OccupantType occupantType,
        OrgMessage message)
    {
        if (occupantType == OccupantType.AiAgent
            && message is Hive.Domain.Messaging.Directive directive)
        {
            var runtimeConfiguration = _runtimeConfiguration
                ?? throw new InvalidOperationException(
                    $"PositionActor '{PersistenceId}' cannot dispatch an AI directive before runtime configuration is loaded.");

            return AiDirectiveProcessingRequest.Create(
                EntityId,
                runtimeConfiguration,
                _state,
                occupant,
                directive);
        }

        return message;
    }

    private void BeginConfigurationLoad()
    {
        _operationalState = PositionOperationalState.LoadingConfiguration;
        _configurationBlockReason = null;
        PublishProjection(new PositionRecovered(EntityId, _state.LastConfigurationStamp, _clock()));

        var self = Self;
        _ = LoadConfigurationAsync(self);
    }

    private async Task LoadConfigurationAsync(IActorRef self)
    {
        try
        {
            var result = await _configurationProvider
                .LoadAsync(EntityId, CancellationToken.None)
                .ConfigureAwait(false);
            self.Tell(new PositionConfigurationLoadCompleted(result));
        }
        catch (Exception exception)
        {
            self.Tell(new PositionConfigurationLoadFailed(exception));
        }
    }

    private void HandleConfigurationLoadCompleted(PositionConfigurationLoadCompleted completed)
    {
        var compatibility = PositionConfigurationCompatibility.Evaluate(
            _state.LastConfigurationStamp,
            completed.Result,
            EntityId);

        switch (compatibility.Decision)
        {
            case PositionConfigurationCompatibilityDecision.ApplyNewConfiguration:
                var configurationToApply = compatibility.Configuration
                    ?? throw new InvalidOperationException("Apply-new-configuration decision must include a configuration.");
                Persist(
                    new PositionConfigurationApplied(configurationToApply.Stamp, _clock()),
                    persisted =>
                    {
                        ApplyPersisted(persisted);
                        MarkReady(configurationToApply);
                    });
                break;

            case PositionConfigurationCompatibilityDecision.AlreadyApplied:
                var alreadyAppliedConfiguration = compatibility.Configuration
                    ?? throw new InvalidOperationException("Already-applied configuration decision must include a configuration.");
                MarkReady(alreadyAppliedConfiguration);
                break;

            case PositionConfigurationCompatibilityDecision.Blocked:
                var blockReason = compatibility.BlockReason
                    ?? throw new InvalidOperationException("Blocked configuration decision must include a reason.");
                _operationalState = PositionOperationalState.ConfigurationBlocked;
                _configurationBlockReason = blockReason;
                PublishProjection(new PositionConfigurationRejected(
                    EntityId,
                    blockReason,
                    _state.LastConfigurationStamp,
                    compatibility.Configuration?.Stamp,
                    _clock()));
                break;

            case PositionConfigurationCompatibilityDecision.TechnicalFailure:
                throw new PositionConfigurationGateException(
                    PersistenceId,
                    compatibility.TechnicalException!);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(completed),
                    compatibility.Decision,
                    "Unknown position configuration compatibility decision.");
        }
    }

    private void MarkReady(PositionRuntimeConfiguration configuration)
    {
        _runtimeConfiguration = configuration;
        _operationalState = PositionOperationalState.Ready;
        _configurationBlockReason = null;
        PersistPendingDispatches(() =>
        {
            PublishProjection(new PositionReactivated(EntityId, _state.LastConfigurationStamp, _clock()));
            Stash.UnstashAll();
        });
    }

    private void WhenReady(Action handler)
    {
        if (_operationalState == PositionOperationalState.Ready)
        {
            handler();
            return;
        }

        if (_operationalState == PositionOperationalState.ConfigurationBlocked)
        {
            return;
        }

        Stash.Stash();
    }
    private void PersistPassivationIfAllowed(RequestPassivation command)
    {
        if (_passivationRequested)
        {
            return;
        }

        var configuration = _runtimeConfiguration
            ?? throw new InvalidOperationException(
                $"PositionActor '{PersistenceId}' cannot evaluate passivation before runtime configuration is loaded.");

        var decision = _state.EvaluatePassivation(configuration);
        if (!decision.IsAllowed)
        {
            return;
        }

        Persist(new PositionPassivated(_clock(), command.Reason), persisted =>
        {
            ApplyPersisted(persisted);
            _passivationRequested = true;
            Context.Parent.Tell(new Passivate(PositionPassivationStop.Instance));
        });
    }

    private PositionRuntimeStatus RuntimeStatus() => new(
        _operationalState,
        _configurationBlockReason,
        _state.LastConfigurationStamp);

    private static MessageProcessingCompletionStatus CompletionStatus(
        PositionOccupantProcessingStatus status) =>
        status switch
        {
            PositionOccupantProcessingStatus.Completed => MessageProcessingCompletionStatus.Completed,
            PositionOccupantProcessingStatus.Failed => MessageProcessingCompletionStatus.Failed,
            PositionOccupantProcessingStatus.Escalated => MessageProcessingCompletionStatus.Escalated,
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown occupant processing completion status."),
        };

    private void PublishProjection(PositionProjectionEvent @event)
    {
        _projectionPublisher?.Publish(@event);
        Context.System.EventStream.Publish(@event);
    }

    private IActorRef ResolveOccupant(OccupantId occupant, OccupantType occupantType)
    {
        var key = new PositionOccupantKey(occupant, occupantType);
        if (_occupantActors.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var actor = Context.ActorOf(_occupantFactory.Create(occupant, occupantType), ChildName(key));
        _occupantActors.Add(key, actor);
        return actor;
    }

    private PositionOccupantKey? CurrentOccupantKey() =>
        _state.Occupant is { } occupant && _state.OccupantType is { } occupantType
            ? new PositionOccupantKey(occupant, occupantType)
            : null;

    private void StopObsoleteOccupant(
        PositionOccupantKey? previous,
        PositionOccupantKey current)
    {
        if (previous is not { } previousKey || previousKey == current)
        {
            return;
        }

        if (_occupantActors.Remove(previousKey, out var actor))
        {
            Context.Stop(actor);
        }
    }

    private static string ChildName(PositionOccupantKey key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{key.OccupantType}:{key.Occupant.Value}")))[..16];

        return $"occupant-{key.OccupantType.ToString().ToLowerInvariant()}-{hash.ToLowerInvariant()}";
    }

    private sealed class UnavailableConfigurationProvider(string reason) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PositionRuntimeConfigurationLoadResult.TechnicalFailure(
                new InvalidOperationException(reason)));
    }

    private readonly record struct PositionOccupantKey(
        OccupantId Occupant,
        OccupantType OccupantType);
}

internal sealed record GetPositionState
{
    public static GetPositionState Instance { get; } = new();
}

internal sealed record GetPositionRuntimeStatus
{
    public static GetPositionRuntimeStatus Instance { get; } = new();
}

internal sealed record PositionConfigurationLoadCompleted(
    PositionRuntimeConfigurationLoadResult Result);

internal sealed record PositionConfigurationLoadFailed(Exception Cause);

internal sealed record RetainedActionResumeCompleted(
    RetainedActionId ActionId,
    Guid AttemptId,
    RetainedActionResumeResult Result);

internal sealed record RetainedActionResumeFailed(
    RetainedActionId ActionId,
    Guid AttemptId,
    Exception Cause);

internal sealed record PositionRuntimeStatus(
    PositionOperationalState OperationalState,
    PositionConfigurationBlockReason? BlockReason,
    PositionConfigurationStamp? LastConfigurationStamp);

internal sealed record PositionPassivationStop
{
    public static PositionPassivationStop Instance { get; } = new();

    private PositionPassivationStop()
    {
    }
}

internal sealed class PositionConfigurationGateException : Exception
{
    public PositionConfigurationGateException(string persistenceId, Exception innerException)
        : base($"PositionActor '{persistenceId}' could not load a compatible runtime configuration.", innerException)
    {
    }
}
