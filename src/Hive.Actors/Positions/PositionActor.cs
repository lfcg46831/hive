using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using Akka.Cluster.Sharding;
using Akka.Persistence;
using Akka.Pattern;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.OccupantChannels;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

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
    private readonly IOccupantReplyMessageValidator _occupantReplyValidator;
    private readonly IPositionMessageEmitter _messageEmitter;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<PositionOccupantKey, IActorRef> _occupantActors = new();
    private readonly HashSet<Guid> _handledResumeAttempts = [];
    private readonly HashSet<RetainedActionId> _resumingActions = [];
    private readonly HashSet<MessageId> _pendingOccupantReplyIds = [];
    private readonly HashSet<MessageId> _pendingApprovalRequestIds = [];
    private readonly HashSet<MessageId> _activeOccupantChannelDeliveries = [];

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
        : this(
            entityId,
            configurationProvider,
            occupantFactory,
            projectionPublisher,
            clock,
            resumeCoordinator,
            occupantReplyValidator: null,
            messageEmitter: null)
    {
    }

    public PositionActor(
        string entityId,
        IPositionConfigurationProvider configurationProvider,
        IPositionOccupantFactory occupantFactory,
        IPositionProjectionPublisher? projectionPublisher,
        Func<DateTimeOffset> clock,
        RetainedActionResumeCoordinator? resumeCoordinator,
        IOccupantReplyMessageValidator? occupantReplyValidator,
        IPositionMessageEmitter? messageEmitter)
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
        _occupantReplyValidator = occupantReplyValidator
            ?? UnavailableOccupantReplyMessageValidator.Instance;
        _messageEmitter = messageEmitter ?? ShardedPositionMessageEmitter.Instance;
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
        Command<PositionOccupantChannelDeliveryReported>(reported =>
            WhenReady(() => PersistOccupantChannelDeliveryResult(reported)));
        Command<PositionOccupantMessageHandoff>(handoff =>
            WhenReady(() => BeginOccupantMessageHandoff(handoff, Sender)));
        Command<PositionOccupantMessageDeliveryCompleted>(HandleOccupantMessageDeliveryCompleted);
        Command<PositionOccupantMessageDeliveryFailed>(HandleOccupantMessageDeliveryFailed);
        Command<AcceptMessage>(command =>
        {
            var replyTo = Sender;
            WhenReady(() =>
            {
                if (_state.ProcessedMessages.Contains(command.Message.Id))
                {
                    PublishProjection(new PositionMessageDuplicateRejected(
                        EntityId,
                        command.Message.Id,
                        command.Message.Thread,
                        _clock()));
                    ReplyToAcceptMessageIfRequested(
                        replyTo,
                        AcceptMessageResult.AlreadyAccepted(command.Message.Id));
                    return;
                }

                PersistAcceptedMessage(
                    command.Message,
                    () => ReplyToAcceptMessageIfRequested(
                        replyTo,
                        AcceptMessageResult.Accepted(command.Message.Id)));
            });
        });
        Command<EmitOccupantReply>(command =>
            WhenReady(() => BeginOccupantReplyEmission(command, Sender)));
        Command<EmitOccupantApprovalDecision>(command =>
            WhenReady(() => BeginOccupantApprovalDecisionEmission(command, Sender)));
        Command<OccupantReplyValidationCompleted>(HandleOccupantReplyValidationCompleted);
        Command<OccupantReplyValidationFailed>(failed =>
        {
            _pendingOccupantReplyIds.Remove(failed.ReplyMessageId);
            failed.ReplyTo.Tell(new Status.Failure(failed.Cause));
        });
        Command<OccupantApprovalDecisionValidationCompleted>(
            HandleOccupantApprovalDecisionValidationCompleted);
        Command<OccupantApprovalDecisionValidationFailed>(failed =>
        {
            _pendingOccupantReplyIds.Remove(failed.DecisionMessageId);
            _pendingApprovalRequestIds.Remove(failed.RequestId);
            failed.ReplyTo.Tell(new Status.Failure(failed.Cause));
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
                PersistAndApply(new ShortMemoryUpdated(
                    command.Key,
                    command.Value,
                    _clock(),
                    command.ContextScope))));
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
        Command<PersistDirectiveCheckpoint>(command =>
            WhenReady(() => PersistCheckpoint(command)));
        Command<ScheduleOccupantReminder>(command =>
            WhenReady(() => PersistOccupantReminderSchedule(command)));
        Command<MarkOccupantReminderSent>(command =>
            WhenReady(() => PersistOccupantReminderSent(command)));
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

    private void PersistAcceptedMessage(OrgMessage message, Action? afterPersisted = null)
    {
        var events = new List<PositionEvent>
        {
            new MessageReceived(message, _clock()),
        };

        if (TryGetCurrentOccupantActivation(out var activation))
        {
            events.Add(new MessageDispatched(
                message.Id,
                message.Thread,
                activation.Occupant,
                activation.OccupantType,
                _clock()));
        }

        PersistEvents(events, afterPersisted);
    }

    private void ReplyToAcceptMessageIfRequested(
        IActorRef replyTo,
        AcceptMessageResult result)
    {
        if (replyTo.Equals(ActorRefs.Nobody)
            || replyTo.Equals(Context.System.DeadLetters))
        {
            return;
        }

        replyTo.Tell(result);
    }

    private void BeginOccupantReplyEmission(EmitOccupantReply command, IActorRef replyTo)
    {
        var existing = _state.OccupantReplies.FirstOrDefault(
            reply => reply.Message.Id == command.ReplyMessageId);
        if (existing is not null)
        {
            if (Matches(existing, command))
            {
                _messageEmitter.Emit(Context.System, existing.Message);
                replyTo.Tell(OccupantReplyEmissionResult.Accepted(
                    existing.SourceMessageId,
                    existing.Message));
            }
            else
            {
                replyTo.Tell(Rejected(
                    command.SourceMessageId,
                    "reply-message-id-conflict",
                    "replyMessageId",
                    RejectionReason.Duplicate));
            }

            return;
        }

        var source = _state.Inbox
            .Concat(_state.MaterializedHistory)
            .FirstOrDefault(message => message.Id == command.SourceMessageId);
        if (source is null)
        {
            replyTo.Tell(Rejected(
                command.SourceMessageId,
                "source-message-not-found",
                "sourceMessageId",
                RejectionReason.InvalidContract));
            return;
        }

        if (source.OrganizationId != EntityId.Organization
            || source.To is not PositionEndpointRef destination
            || destination.PositionId != EntityId.Position)
        {
            replyTo.Tell(Rejected(
                command.SourceMessageId,
                "source-message-not-owned",
                "sourceMessageId",
                RejectionReason.Unauthorized));
            return;
        }

        if (!TryCreateOccupantReply(command, source, out var message, out var rejection))
        {
            replyTo.Tell(rejection!);
            return;
        }

        if (!_pendingOccupantReplyIds.Add(command.ReplyMessageId))
        {
            replyTo.Tell(Rejected(
                command.SourceMessageId,
                "reply-emission-in-progress",
                "replyMessageId",
                RejectionReason.Duplicate));
            return;
        }

        _occupantReplyValidator
            .ValidateAsync(_state, message!, CancellationToken.None)
            .AsTask()
            .PipeTo(
                Self,
                success: validation => new OccupantReplyValidationCompleted(
                    command,
                    replyTo,
                    message!,
                    validation),
                failure: exception => new OccupantReplyValidationFailed(
                    command.ReplyMessageId,
                    replyTo,
                    exception));
    }

    private void BeginOccupantMessageHandoff(
        PositionOccupantMessageHandoff handoff,
        IActorRef replyTo)
    {
        var existing = _state.OccupantReplies.FirstOrDefault(
            reply => reply.Message.Id == handoff.Message.Id);
        if (existing is not null)
        {
            if (Matches(existing, handoff))
            {
                if (!_pendingOccupantReplyIds.Add(handoff.Message.Id))
                {
                    replyTo.Tell(Rejected(
                        handoff.SourceMessageId,
                        "result-message-handoff-in-progress",
                        "message.id",
                        RejectionReason.Duplicate));
                    return;
                }

                BeginConfirmedHandoffDelivery(existing, replyTo);
            }
            else
            {
                replyTo.Tell(Rejected(
                    handoff.SourceMessageId,
                    "result-message-id-conflict",
                    "message.id",
                    RejectionReason.Duplicate));
            }

            return;
        }

        var source = _state.Inbox
            .Concat(_state.MaterializedHistory)
            .FirstOrDefault(message => message.Id == handoff.SourceMessageId);
        if (source is null)
        {
            replyTo.Tell(Rejected(
                handoff.SourceMessageId,
                "source-message-not-found",
                "sourceMessageId",
                RejectionReason.InvalidContract));
            return;
        }

        if (source.OrganizationId != EntityId.Organization
            || source.To is not PositionEndpointRef sourceDestination
            || sourceDestination.PositionId != EntityId.Position)
        {
            replyTo.Tell(Rejected(
                handoff.SourceMessageId,
                "source-message-not-owned",
                "sourceMessageId",
                RejectionReason.Unauthorized));
            return;
        }

        if (handoff.Message.OrganizationId != EntityId.Organization
            || handoff.Message.Thread != source.Thread
            || handoff.Message.From is not PositionEndpointRef messageSource
            || messageSource.PositionId != EntityId.Position)
        {
            replyTo.Tell(Rejected(
                handoff.SourceMessageId,
                "result-message-correlation-invalid",
                "message",
                RejectionReason.InvalidContract));
            return;
        }

        if (handoff.Author.Kind == OccupantReplyAuthorKind.AiAgent
            && (_state.Occupant is null
                || !string.Equals(
                    handoff.Author.SubjectId,
                    _state.Occupant.Value,
                    StringComparison.Ordinal)))
        {
            replyTo.Tell(Rejected(
                handoff.SourceMessageId,
                "result-message-author-not-current",
                "author.subjectId",
                RejectionReason.Unauthorized));
            return;
        }

        if (!_pendingOccupantReplyIds.Add(handoff.Message.Id))
        {
            replyTo.Tell(Rejected(
                handoff.SourceMessageId,
                "result-message-handoff-in-progress",
                "message.id",
                RejectionReason.Duplicate));
            return;
        }

        OccupantReplyEmitted accepted;
        IReadOnlyList<PositionEvent> events;
        try
        {
            accepted = new OccupantReplyEmitted(
                handoff.SourceMessageId,
                handoff.Author,
                handoff.Message,
                _clock());
            events = CreateHandoffEvents(accepted, handoff.PositionCommands);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _pendingOccupantReplyIds.Remove(handoff.Message.Id);
            replyTo.Tell(Rejected(
                handoff.SourceMessageId,
                "result-message-effects-invalid",
                "positionCommands",
                RejectionReason.InvalidContract));
            return;
        }

        PersistEvents(events, () =>
        {
            BeginConfirmedHandoffDelivery(accepted, replyTo);
        });
    }

    private void BeginConfirmedHandoffDelivery(
        OccupantReplyEmitted handoff,
        IActorRef replyTo)
    {
        _messageEmitter
            .EmitConfirmedAsync(Context.System, handoff.Message)
            .AsTask()
            .PipeTo(
                Self,
                success: result => new PositionOccupantMessageDeliveryCompleted(
                    handoff,
                    replyTo,
                    result),
                failure: exception => new PositionOccupantMessageDeliveryFailed(
                    handoff,
                    replyTo,
                    exception));
    }

    private void HandleOccupantMessageDeliveryCompleted(
        PositionOccupantMessageDeliveryCompleted completed)
    {
        _pendingOccupantReplyIds.Remove(completed.Handoff.Message.Id);
        if (!completed.Result.IsAccepted
            || completed.Result.MessageId != completed.Handoff.Message.Id)
        {
            completed.ReplyTo.Tell(new Status.Failure(new InvalidOperationException(
                $"Destination did not confirm result message '{completed.Handoff.Message.Id}' acceptance.")));
            return;
        }

        completed.ReplyTo.Tell(OccupantReplyEmissionResult.Accepted(
            completed.Handoff.SourceMessageId,
            completed.Handoff.Message));
    }

    private void HandleOccupantMessageDeliveryFailed(
        PositionOccupantMessageDeliveryFailed failed)
    {
        _pendingOccupantReplyIds.Remove(failed.Handoff.Message.Id);
        failed.ReplyTo.Tell(new Status.Failure(failed.Cause));
    }

    private IReadOnlyList<PositionEvent> CreateHandoffEvents(
        OccupantReplyEmitted accepted,
        IReadOnlyList<PositionCommand> commands)
    {
        var occurredAt = accepted.OccurredAt;
        var events = new List<PositionEvent>(commands.Count + 1)
        {
            accepted,
        };

        foreach (var command in commands)
        {
            events.Add(command switch
            {
                OpenTask open => new TaskCreated(
                    open.TaskId,
                    open.Thread,
                    open.Title,
                    open.Priority,
                    occurredAt,
                    open.Deadline,
                    open.CausedBy),
                UpdateTask update => new TaskUpdated(
                    update.TaskId,
                    update.Note,
                    occurredAt,
                    update.Priority,
                    update.Deadline),
                CompleteTask complete => new TaskCompleted(
                    complete.TaskId,
                    occurredAt,
                    complete.Summary),
                UpdateShortMemory memory => new ShortMemoryUpdated(
                    memory.Key,
                    memory.Value,
                    occurredAt,
                    memory.ContextScope),
                _ => throw new InvalidOperationException(
                    $"Position command '{command.GetType().Name}' is not supported by the durable result handoff."),
            });
        }

        return events;
    }

    private void HandleOccupantReplyValidationCompleted(OccupantReplyValidationCompleted completed)
    {
        _pendingOccupantReplyIds.Remove(completed.Command.ReplyMessageId);
        if (!completed.Validation.IsValid)
        {
            completed.ReplyTo.Tell(OccupantReplyEmissionResult.Rejected(
                completed.Command.SourceMessageId,
                completed.Validation.Errors
                    .Select(error => new OccupantReplyEmissionError(
                        error.Code,
                        error.Path,
                        error.Reason))
                    .ToArray()));
            return;
        }

        var emitted = new OccupantReplyEmitted(
            completed.Command.SourceMessageId,
            completed.Command.Author,
            completed.Message,
            _clock());
        Persist(emitted, persisted =>
        {
            ApplyPersisted(persisted);
            try
            {
                _messageEmitter.Emit(Context.System, persisted.Message);
                completed.ReplyTo.Tell(OccupantReplyEmissionResult.Accepted(
                    persisted.SourceMessageId,
                    persisted.Message));
            }
            catch (Exception exception)
            {
                completed.ReplyTo.Tell(new Status.Failure(exception));
            }
        });
    }

    private void BeginOccupantApprovalDecisionEmission(
        EmitOccupantApprovalDecision command,
        IActorRef replyTo)
    {
        var request = _state.Inbox
            .Concat(_state.MaterializedHistory)
            .OfType<ApprovalRequest>()
            .FirstOrDefault(candidate => candidate.Id == command.RequestId);
        var decision = new ApprovalDecision(
            command.DecisionMessageId,
            EntityId.Organization,
            new PositionEndpointRef(EntityId.Position),
            request?.From ?? new PositionEndpointRef(command.RequesterPositionId),
            request?.Thread ?? command.RequestThread,
            request?.Priority ?? command.RequestPriority,
            schemaVersion: 1,
            _clock(),
            deadline: null,
            command.RequestId,
            command.Approved,
            command.Reason);

        var existing = _state.OccupantReplies.FirstOrDefault(
            reply => reply.Message.Id == command.DecisionMessageId);
        if (existing is not null)
        {
            if (Matches(existing, command))
            {
                _messageEmitter.Emit(Context.System, existing.Message);
                replyTo.Tell(OccupantReplyEmissionResult.Accepted(
                    existing.SourceMessageId,
                    existing.Message));
            }
            else
            {
                replyTo.Tell(RejectApprovalDecision(
                    command,
                    decision,
                    request,
                    ValidationResult.Create([new ValidationError(
                        "decision-message-id-conflict",
                        "decisionMessageId",
                        RejectionReason.Duplicate)])));
            }

            return;
        }

        if (!_pendingOccupantReplyIds.Add(command.DecisionMessageId))
        {
            replyTo.Tell(RejectApprovalDecision(
                command,
                decision,
                request,
                ValidationResult.Create([new ValidationError(
                    "approval-decision-emission-in-progress",
                    "decisionMessageId",
                    RejectionReason.Duplicate)])));
            return;
        }

        if (!_pendingApprovalRequestIds.Add(command.RequestId))
        {
            _pendingOccupantReplyIds.Remove(command.DecisionMessageId);
            replyTo.Tell(RejectApprovalDecision(
                command,
                decision,
                request,
                ValidationResult.Create([new ValidationError(
                    "approval-decision-in-progress",
                    "requestId",
                    RejectionReason.Duplicate)])));
            return;
        }

        _occupantReplyValidator
            .ValidateAsync(_state, decision, CancellationToken.None)
            .AsTask()
            .PipeTo(
                Self,
                success: validation => new OccupantApprovalDecisionValidationCompleted(
                    command,
                    replyTo,
                    decision,
                    request,
                    validation),
                failure: exception => new OccupantApprovalDecisionValidationFailed(
                    command.RequestId,
                    command.DecisionMessageId,
                    replyTo,
                    exception));
    }

    private void HandleOccupantApprovalDecisionValidationCompleted(
        OccupantApprovalDecisionValidationCompleted completed)
    {
        _pendingOccupantReplyIds.Remove(completed.Command.DecisionMessageId);
        _pendingApprovalRequestIds.Remove(completed.Command.RequestId);
        if (!completed.Validation.IsValid)
        {
            completed.ReplyTo.Tell(RejectApprovalDecision(
                completed.Command,
                completed.Decision,
                completed.Request,
                completed.Validation));
            return;
        }

        var emitted = new OccupantReplyEmitted(
            completed.Command.RequestId,
            completed.Command.Author,
            completed.Decision,
            _clock());
        Persist(emitted, persisted =>
        {
            ApplyPersisted(persisted);
            try
            {
                _messageEmitter.Emit(Context.System, persisted.Message);
                completed.ReplyTo.Tell(OccupantReplyEmissionResult.Accepted(
                    persisted.SourceMessageId,
                    persisted.Message));
            }
            catch (Exception exception)
            {
                completed.ReplyTo.Tell(new Status.Failure(exception));
            }
        });
    }

    private OccupantReplyEmissionResult RejectApprovalDecision(
        EmitOccupantApprovalDecision command,
        ApprovalDecision decision,
        ApprovalRequest? request,
        ValidationResult validation)
    {
        var context = RoutingValidationContext.ForMessage(decision);
        if (request is not null)
        {
            context = context.WithGovernance(
                request.Policy,
                appliedVersion: null,
                request.To);
        }

        var rejection = RoutingRejection.Create(
            context,
            validation);
        PublishProjection(new PositionApprovalDecisionRejected(
            EntityId,
            command.RequestId,
            command.Author,
            rejection,
            _clock()));
        return OccupantReplyEmissionResult.Rejected(
            command.RequestId,
            validation.Errors
                .Select(error => new OccupantReplyEmissionError(
                    error.Code,
                    error.Path,
                    error.Reason))
                .ToArray());
    }

    private bool TryCreateOccupantReply(
        EmitOccupantReply command,
        OrgMessage source,
        out OrgMessage? message,
        out OccupantReplyEmissionResult? rejection)
    {
        message = null;
        rejection = null;
        var from = new PositionEndpointRef(EntityId.Position);
        var to = source.From;
        var sentAt = _clock();

        switch (source)
        {
            case OrgDirective directive:
                if (command.ReportKind is not { } reportKind)
                {
                    rejection = Rejected(
                        command.SourceMessageId,
                        "report-kind-required",
                        "reportKind",
                        RejectionReason.InvalidContract);
                    return false;
                }

                if (command.ReplyDirectiveId is not null)
                {
                    rejection = Rejected(
                        command.SourceMessageId,
                        "reply-directive-id-not-applicable",
                        "replyDirectiveId",
                        RejectionReason.InvalidContract);
                    return false;
                }

                message = new Report(
                    command.ReplyMessageId,
                    source.OrganizationId,
                    from,
                    to,
                    source.Thread,
                    source.Priority,
                    schemaVersion: 1,
                    sentAt,
                    deadline: null,
                    directive.DirectiveId,
                    reportKind,
                    command.Body);
                return true;

            case PeerRequest request:
                if (command.ReportKind is not null || command.ReplyDirectiveId is not null)
                {
                    rejection = Rejected(
                        command.SourceMessageId,
                        "reply-metadata-not-applicable",
                        "$",
                        RejectionReason.InvalidContract);
                    return false;
                }

                message = new PeerResponse(
                    command.ReplyMessageId,
                    source.OrganizationId,
                    from,
                    to,
                    source.Thread,
                    source.Priority,
                    schemaVersion: 1,
                    sentAt,
                    deadline: null,
                    request.Id,
                    command.Body);
                return true;

            case Escalation:
                if (command.ReportKind is not null)
                {
                    rejection = Rejected(
                        command.SourceMessageId,
                        "report-kind-not-applicable",
                        "reportKind",
                        RejectionReason.InvalidContract);
                    return false;
                }

                if (command.ReplyDirectiveId is not { } replyDirectiveId)
                {
                    rejection = Rejected(
                        command.SourceMessageId,
                        "reply-directive-id-required",
                        "replyDirectiveId",
                        RejectionReason.InvalidContract);
                    return false;
                }

                message = new OrgDirective(
                    command.ReplyMessageId,
                    source.OrganizationId,
                    from,
                    to,
                    source.Thread,
                    source.Priority,
                    schemaVersion: 1,
                    sentAt,
                    deadline: null,
                    replyDirectiveId,
                    parentDirectiveId: null,
                    command.Body,
                    $"Human response to escalation {source.Id}.");
                return true;

            default:
                rejection = Rejected(
                    command.SourceMessageId,
                    "reply-not-supported",
                    "sourceMessageId",
                    RejectionReason.InvalidContract);
                return false;
        }
    }

    private static OccupantReplyEmissionResult Rejected(
        MessageId sourceMessageId,
        string code,
        string path,
        RejectionReason reason) =>
        OccupantReplyEmissionResult.Rejected(
            sourceMessageId,
            new OccupantReplyEmissionError(code, path, reason));

    private static bool Matches(OccupantReplyEmitted emitted, EmitOccupantReply command) =>
        emitted.Message is Report or PeerResponse or OrgDirective
        && emitted.SourceMessageId == command.SourceMessageId
        && emitted.Author == command.Author
        && string.Equals(ReplyBody(emitted.Message), command.Body, StringComparison.Ordinal)
        && (emitted.Message is Report report ? report.Kind : null) == command.ReportKind
        && (emitted.Message is OrgDirective directive ? directive.DirectiveId : null)
            == command.ReplyDirectiveId;

    private static bool Matches(
        OccupantReplyEmitted emitted,
        PositionOccupantMessageHandoff handoff) =>
        emitted.SourceMessageId == handoff.SourceMessageId
        && emitted.Author == handoff.Author
        && emitted.Message == handoff.Message;

    private static bool Matches(
        OccupantReplyEmitted emitted,
        EmitOccupantApprovalDecision command) =>
        emitted.SourceMessageId == command.RequestId
        && emitted.Author == command.Author
        && emitted.Message is ApprovalDecision decision
        && decision.RequestId == command.RequestId
        && decision.Approved == command.Approved
        && string.Equals(decision.Reason, command.Reason, StringComparison.Ordinal);

    private static string ReplyBody(OrgMessage message) =>
        message switch
        {
            Report report => report.Body,
            PeerResponse response => response.Body,
            OrgDirective directive => directive.Objective,
            _ => throw new InvalidOperationException(
                $"Unsupported persisted occupant reply '{message.GetType().Name}'."),
        };

    private void PersistOccupantChange(ChangeOccupant command)
    {
        if (command.Type == OccupantType.Human &&
            !TryGetConfiguredHumanActivation(command.Occupant, out _))
        {
            StopInactiveHumanOccupants(command.Occupant);
            return;
        }

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

    private void PersistOccupantChannelDeliveryResult(
        PositionOccupantChannelDeliveryReported reported)
    {
        if (!_state.OccupantNotifications.TryGetValue(reported.MessageId, out var notification) ||
            notification.Status != OccupantNotificationDeliveryStatus.Requested ||
            notification.Thread != reported.ThreadId ||
            notification.Occupant != reported.OccupantId ||
            notification.User != reported.UserId ||
            notification.Binding != reported.BindingId)
        {
            return;
        }

        PositionEvent terminal = reported.Result.IsSuccess && reported.BindingId is { } binding
            ? new OccupantChannelDeliveryConfirmed(
                notification.Message,
                notification.Thread,
                notification.Occupant,
                notification.User,
                binding,
                _clock())
            : new OccupantChannelDeliveryFailed(
                notification.Message,
                notification.Thread,
                notification.Occupant,
                notification.User,
                notification.Binding,
                reported.Result.Error ?? new OccupantChannelDeliveryError(
                    OccupantChannelDeliveryErrorCode.DeliveryRejected,
                    isRetryable: false),
                _clock());

        Persist(terminal, persisted =>
        {
            ApplyPersisted(persisted);
            _activeOccupantChannelDeliveries.Remove(reported.MessageId);
            Context.System.EventStream.Publish(reported);
        });
    }

    private void PersistOccupantReminderSchedule(ScheduleOccupantReminder command)
    {
        if (!_state.OccupantNotifications.TryGetValue(command.Message, out var notification) ||
            notification.Reminders.Any(reminder => reminder.Id == command.Reminder))
        {
            return;
        }

        PersistAndApply(new OccupantReminderScheduled(
            notification.Message,
            notification.Thread,
            notification.Occupant,
            notification.User,
            notification.Binding,
            command.Reminder,
            command.ScheduledFor,
            _clock()));
    }

    private void PersistOccupantReminderSent(MarkOccupantReminderSent command)
    {
        if (!_state.OccupantNotifications.TryGetValue(command.Message, out var notification))
        {
            return;
        }

        var reminder = notification.Reminders.FirstOrDefault(item => item.Id == command.Reminder);
        if (reminder is null || reminder.SentAt is not null)
        {
            return;
        }

        PersistAndApply(new OccupantReminderSent(
            notification.Message,
            notification.Thread,
            notification.Occupant,
            notification.User,
            command.Binding,
            command.Reminder,
            _clock()));
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

    private void PersistCheckpoint(PersistDirectiveCheckpoint command)
    {
        var replyTo = Sender;
        var decision = _state.EvaluateDirectiveCheckpointPersistence(
            EntityId,
            command.Checkpoint);
        if (decision != DirectiveCheckpointPersistenceDecision.Persist)
        {
            replyTo.Tell(new DirectiveCheckpointPersistenceResult(
                command.Checkpoint.Correlation.DirectiveId,
                command.Checkpoint.Revision,
                decision));
            return;
        }

        Persist(
            new DirectiveCheckpointPersisted(command.Checkpoint, _clock()),
            persisted =>
            {
                ApplyPersisted(persisted);
                replyTo.Tell(new DirectiveCheckpointPersistenceResult(
                    command.Checkpoint.Correlation.DirectiveId,
                    command.Checkpoint.Revision,
                    DirectiveCheckpointPersistenceDecision.Persist));
            });
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
        if (!TryGetCurrentOccupantActivation(out var activation))
        {
            if (_state.Occupant is { } inactiveOccupant &&
                _state.OccupantType == OccupantType.Human)
            {
                StopInactiveHumanOccupants(inactiveOccupant);
            }

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
                activation.Occupant,
                activation.OccupantType,
                _clock()))
            .ToArray();

        PersistEvents(events, () =>
        {
            foreach (var message in alreadyDispatched)
            {
                var dispatch = new MessageDispatched(
                    message.Id,
                    message.Thread,
                    activation.Occupant,
                    activation.OccupantType,
                    _clock());
                if (activation.OccupantType == OccupantType.Human)
                {
                    PersistHumanOccupantNotificationRequest(message, dispatch);
                }
                else
                {
                    DeliverToOccupant(message, dispatch);
                }
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
            StopObsoleteOccupants(changed.Occupant, changed.Type);
        }

        if (persisted is MessageDispatched dispatchEvent && dispatchedMessage is not null)
        {
            if (dispatchEvent.OccupantType == OccupantType.Human)
            {
                PersistHumanOccupantNotificationRequest(dispatchedMessage, dispatchEvent);
            }
            else
            {
                DeliverToOccupant(dispatchedMessage, dispatchEvent);
            }
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
        MessageDispatched dispatch)
    {
        if (!TryGetActivation(dispatch.Occupant, dispatch.OccupantType, out var activation))
        {
            if (dispatch.OccupantType == OccupantType.Human)
            {
                StopInactiveHumanOccupants(dispatch.Occupant);
            }

            return;
        }

        ResolveOccupant(activation)
            .Tell(CreateOccupantPayload(activation, dispatch, message));
    }

    private void PersistHumanOccupantNotificationRequest(
        OrgMessage message,
        MessageDispatched dispatch)
    {
        if (_state.OccupantNotifications.ContainsKey(dispatch.Message) ||
            !TryGetActivation(dispatch.Occupant, OccupantType.Human, out var activation) ||
            !_activeOccupantChannelDeliveries.Add(dispatch.Message))
        {
            return;
        }

        var runtimeConfiguration = _runtimeConfiguration
            ?? throw new InvalidOperationException(
                $"PositionActor '{PersistenceId}' cannot request human notification before runtime configuration is loaded.");
        var identity = runtimeConfiguration.Occupant.HumanIdentity
            ?? throw new InvalidOperationException(
                $"PositionActor '{PersistenceId}' cannot request human notification without an active occupation link.");

        Persist(
            new OccupantChannelDeliveryRequested(
                dispatch.Message,
                dispatch.Thread,
                activation.Occupant,
                identity.UserId,
                identity.ChannelBindingId,
                _clock()),
            persisted =>
            {
                ApplyPersisted(persisted);
                DeliverToOccupant(message, dispatch);
            });
    }

    private void RedeliverRequestedOccupantNotifications()
    {
        foreach (var notification in _state.OccupantNotifications.Values
                     .Where(item => item.Status == OccupantNotificationDeliveryStatus.Requested)
                     .OrderBy(item => item.RequestedAt)
                     .ThenBy(item => item.Message.Value))
        {
            if (!_activeOccupantChannelDeliveries.Add(notification.Message))
            {
                continue;
            }

            var message = _state.Inbox.FirstOrDefault(item => item.Id == notification.Message);
            if (message is null ||
                !TryGetActivation(notification.Occupant, OccupantType.Human, out _))
            {
                _activeOccupantChannelDeliveries.Remove(notification.Message);
                continue;
            }

            DeliverToOccupant(
                message,
                new MessageDispatched(
                    notification.Message,
                    notification.Thread,
                    notification.Occupant,
                    OccupantType.Human,
                    notification.RequestedAt));
        }
    }

    private object CreateOccupantPayload(
        PositionOccupantActivation activation,
        MessageDispatched dispatch,
        OrgMessage message)
    {
        if (activation.OccupantType == OccupantType.AiAgent
            && message is Hive.Domain.Messaging.Directive directive)
        {
            var runtimeConfiguration = _runtimeConfiguration
                ?? throw new InvalidOperationException(
                    $"PositionActor '{PersistenceId}' cannot dispatch an AI directive before runtime configuration is loaded.");

            return AiDirectiveProcessingRequest.Create(
                EntityId,
                runtimeConfiguration,
                _state,
                activation.Occupant,
                directive);
        }

        if (activation.OccupantType == OccupantType.Human)
        {
            var runtimeConfiguration = _runtimeConfiguration
                ?? throw new InvalidOperationException(
                    $"PositionActor '{PersistenceId}' cannot dispatch to a human before runtime configuration is loaded.");
            var humanIdentity = runtimeConfiguration.Occupant.HumanIdentity
                ?? throw new InvalidOperationException(
                    $"PositionActor '{PersistenceId}' cannot dispatch to a human without an active occupation link.");

            return new HumanOccupantChannelDelivery(
                dispatch,
                new OccupantChannelDeliveryContext(
                    EntityId.Organization,
                    EntityId.Position,
                    activation.Occupant,
                    humanIdentity.UserId,
                    humanIdentity.ChannelBindingId,
                    message));
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
        _configurationBlockReason = null;

        if (_state.Occupant is null
            && TryGetConfiguredOccupant(configuration, out var activation))
        {
            Persist(
                new OccupantChanged(
                    activation.Occupant,
                    activation.OccupantType,
                    _clock()),
                persisted =>
                {
                    ApplyPersisted(persisted);
                    CompleteReadyTransition();
                });
            return;
        }

        if (_state.Occupant is { } occupant &&
            _state.OccupantType == OccupantType.Human &&
            !TryGetConfiguredHumanActivation(occupant, out _))
        {
            StopInactiveHumanOccupants(occupant);
        }

        CompleteReadyTransition();
    }

    private void CompleteReadyTransition()
    {
        _operationalState = PositionOperationalState.Ready;
        PersistPendingDispatches(() =>
        {
            RedeliverRequestedOccupantNotifications();
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

    private IActorRef ResolveOccupant(PositionOccupantActivation activation)
    {
        var key = PositionOccupantKey.From(activation);
        StopObsoleteOccupants(key);
        if (_occupantActors.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var actor = Context.ActorOf(_occupantFactory.Create(activation), ChildName(key));
        _occupantActors.Add(key, actor);
        return actor;
    }

    private void StopObsoleteOccupants(OccupantId occupant, OccupantType occupantType)
    {
        var obsolete = _occupantActors.Keys
            .Where(key => key.Occupant != occupant || key.OccupantType != occupantType)
            .ToArray();
        StopOccupants(obsolete);
    }

    private void StopObsoleteOccupants(PositionOccupantKey current)
    {
        var obsolete = _occupantActors.Keys
            .Where(key => key != current)
            .ToArray();
        StopOccupants(obsolete);
    }

    private void StopInactiveHumanOccupants(OccupantId occupant)
    {
        var inactive = _occupantActors.Keys
            .Where(key => key.Occupant == occupant && key.OccupantType == OccupantType.Human)
            .ToArray();
        StopOccupants(inactive);
    }

    private void StopOccupants(IEnumerable<PositionOccupantKey> keys)
    {
        foreach (var key in keys)
        {
            if (_occupantActors.Remove(key, out var actor))
            {
                Context.Stop(actor);
            }
        }
    }

    private bool TryGetCurrentOccupantActivation(out PositionOccupantActivation activation)
    {
        if (_state.Occupant is not { } occupant ||
            _state.OccupantType is not { } occupantType)
        {
            activation = null!;
            return false;
        }

        return TryGetActivation(occupant, occupantType, out activation);
    }

    private bool TryGetActivation(
        OccupantId occupant,
        OccupantType occupantType,
        out PositionOccupantActivation activation)
    {
        if (occupantType == OccupantType.AiAgent)
        {
            activation = PositionOccupantActivation.AiAgent(occupant);
            return true;
        }

        return TryGetConfiguredHumanActivation(occupant, out activation);
    }

    private bool TryGetConfiguredHumanActivation(
        OccupantId occupant,
        out PositionOccupantActivation activation)
    {
        var configured = _runtimeConfiguration?.Occupant;
        if (configured?.Type == OccupantType.Human &&
            configured.ConfiguredIdentity == occupant &&
            configured.HumanIdentity is { } humanIdentity)
        {
            activation = PositionOccupantActivation.Human(occupant, humanIdentity.UserId);
            return true;
        }

        activation = null!;
        return false;
    }

    private static bool TryGetConfiguredOccupant(
        PositionRuntimeConfiguration configuration,
        out PositionOccupantActivation activation)
    {
        var occupant = configuration.Occupant;
        if (occupant.ConfiguredIdentity is not { } configuredIdentity)
        {
            activation = null!;
            return false;
        }

        if (occupant.Type == OccupantType.AiAgent)
        {
            activation = PositionOccupantActivation.AiAgent(configuredIdentity);
            return true;
        }

        if (occupant.Type == OccupantType.Human && occupant.HumanIdentity is { } humanIdentity)
        {
            activation = PositionOccupantActivation.Human(configuredIdentity, humanIdentity.UserId);
            return true;
        }

        activation = null!;
        return false;
    }

    private static string ChildName(PositionOccupantKey key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{key.OccupantType}:{key.Occupant.Value}:{key.UserId?.Value.ToString("N") ?? "none"}")))[..16];

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
        OccupantType OccupantType,
        UserId? UserId)
    {
        public static PositionOccupantKey From(PositionOccupantActivation activation) =>
            new(activation.Occupant, activation.OccupantType, activation.UserId);
    }
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

internal sealed record OccupantReplyValidationCompleted(
    EmitOccupantReply Command,
    IActorRef ReplyTo,
    OrgMessage Message,
    ValidationResult Validation);

internal sealed record OccupantReplyValidationFailed(
    MessageId ReplyMessageId,
    IActorRef ReplyTo,
    Exception Cause);

internal sealed record OccupantApprovalDecisionValidationCompleted(
    EmitOccupantApprovalDecision Command,
    IActorRef ReplyTo,
    ApprovalDecision Decision,
    ApprovalRequest? Request,
    ValidationResult Validation);

internal sealed record OccupantApprovalDecisionValidationFailed(
    MessageId RequestId,
    MessageId DecisionMessageId,
    IActorRef ReplyTo,
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
