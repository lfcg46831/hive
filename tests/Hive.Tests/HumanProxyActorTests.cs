using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.OccupantChannels;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class HumanProxyActorTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 11, 18, 0, 0, TimeSpan.Zero);
    private static readonly PositionEntityId Entity = PositionEntityId.From(
        OrganizationId.From("acme"),
        PositionId.From("delivery-lead"));
    private static readonly PositionConfigurationStamp Stamp =
        new(1, "sha256:human-proxy-v1");
    private static readonly OccupantId Occupant = OccupantId.From("human:delivery-lead");
    private static readonly UserId User =
        UserId.From(Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly OccupantChannelBindingId Binding =
        OccupantChannelBindingId.From(Guid.Parse("22222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    [Fact]
    public async Task Active_human_occupation_delivers_from_persisted_dispatch_and_keeps_inbox_authoritative()
    {
        var message = Message(
            "33333333-cccc-cccc-cccc-cccccccccccc",
            "44444444-dddd-dddd-dddd-dddddddddddd");
        var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
        var requestFactory = new RecordingRequestFactory();
        var system = CreateActorSystem("human-proxy-success");
        var reported = new TaskCompletionSource<PositionOccupantChannelDeliveryReported>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var observer = system.ActorOf(
                Props.Create(() => new DeliveryReportObserver(reported)),
                "delivery-report-observer");
            system.EventStream.Subscribe(observer, typeof(PositionOccupantChannelDeliveryReported));
            var actor = CreatePosition(system, activeLink: true, channel, requestFactory);

            await WaitForReadyAsync(actor);
            var accepted = await actor.Ask<AcceptMessageResult>(
                new AcceptMessage(message),
                Timeout());
            var request = await channel.Request.WaitAsync(Timeout());
            var result = await reported.Task.WaitAsync(Timeout());
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(AcceptMessageDecision.Accepted, accepted.Decision);
            Assert.Equal(Entity.Organization, request.OrganizationId);
            Assert.Equal(Entity.Position, request.PositionId);
            Assert.Equal(Occupant, request.OccupantId);
            Assert.Equal(User, request.UserId);
            Assert.Equal(Binding, request.OccupantChannelBindingId);
            Assert.Equal(message.Id, request.MessageId);
            Assert.Equal(message.Thread, request.ThreadId);
            Assert.Equal(message, requestFactory.Context!.Message);
            Assert.True(result.Result.IsSuccess);
            Assert.Equal(message.Id, result.MessageId);
            Assert.Equal([message.Id], state.Inbox.Select(item => item.Id));
            var notification = Assert.Single(state.OccupantNotifications).Value;
            Assert.Equal(OccupantNotificationDeliveryStatus.Confirmed, notification.Status);
            Assert.Equal(Binding, notification.Binding);
            Assert.Null(notification.Failure);

            await actor.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system);
            Assert.Single(events.OfType<OccupantChanged>());
            Assert.Single(events.OfType<MessageDispatched>());
            Assert.Single(events.OfType<OccupantChannelDeliveryRequested>());
            Assert.Single(events.OfType<OccupantChannelDeliveryConfirmed>());
            Assert.Empty(events.OfType<MessageProcessingCompleted>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Structured_channel_failure_is_returned_to_position_without_completing_work()
    {
        var message = Message(
            "55555555-eeee-eeee-eeee-eeeeeeeeeeee",
            "66666666-ffff-ffff-ffff-ffffffffffff");
        var failure = OccupantChannelDeliveryResult.Failed(
            new OccupantChannelDeliveryError(
                OccupantChannelDeliveryErrorCode.ChannelUnavailable,
                isRetryable: true));
        var channel = new RecordingChannel(failure);
        var system = CreateActorSystem("human-proxy-failure");
        var reported = new TaskCompletionSource<PositionOccupantChannelDeliveryReported>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var observer = system.ActorOf(
                Props.Create(() => new DeliveryReportObserver(reported)),
                "delivery-report-observer");
            system.EventStream.Subscribe(observer, typeof(PositionOccupantChannelDeliveryReported));
            var actor = CreatePosition(
                system,
                activeLink: true,
                channel,
                new RecordingRequestFactory());

            await WaitForReadyAsync(actor);
            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            var result = await reported.Task.WaitAsync(Timeout());
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.True(result.Result.IsFailure);
            Assert.Equal(
                OccupantChannelDeliveryErrorCode.ChannelUnavailable,
                result.Result.Error!.Code);
            Assert.True(result.Result.Error.IsRetryable);
            Assert.Equal([message.Id], state.Inbox.Select(item => item.Id));
            var notification = Assert.Single(state.OccupantNotifications).Value;
            Assert.Equal(OccupantNotificationDeliveryStatus.Failed, notification.Status);
            Assert.Equal(result.Result.Error, notification.Failure);

            await actor.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system);
            Assert.Single(events.OfType<OccupantChannelDeliveryRequested>());
            Assert.Single(events.OfType<OccupantChannelDeliveryFailed>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Human_configuration_without_active_link_never_dispatches_and_keeps_message_in_inbox()
    {
        var message = Message(
            "77777777-1111-2222-3333-444444444444",
            "88888888-5555-6666-7777-888888888888");
        var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
        var system = CreateActorSystem("human-proxy-unlinked");

        try
        {
            var actor = CreatePosition(
                system,
                activeLink: false,
                channel,
                new RecordingRequestFactory());

            await WaitForReadyAsync(actor);
            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Null(state.Occupant);
            Assert.Null(state.OccupantType);
            Assert.Equal([message.Id], state.Inbox.Select(item => item.Id));
            Assert.Empty(state.RecentHistory);
            Assert.False(channel.WasCalled);

            await actor.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system);
            Assert.Empty(events.OfType<OccupantChanged>());
            Assert.Empty(events.OfType<MessageDispatched>());
            Assert.Single(events.OfType<MessageReceived>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Recovered_human_occupant_with_revoked_link_is_inhibited_without_losing_inbox()
    {
        var pending = Message(
            "99999999-1111-2222-3333-444444444444",
            "aaaaaaaa-5555-6666-7777-888888888888");
        var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
        var system = CreateActorSystem("human-proxy-revoked");

        try
        {
            await SeedSnapshotAsync(
                system,
                new PositionSnapshot(
                    At,
                    Occupant,
                    OccupantType.Human,
                    inbox: [pending],
                    recentHistory: [pending.Id],
                    lastConfigurationStamp: Stamp));
            var actor = CreatePosition(
                system,
                activeLink: false,
                channel,
                new RecordingRequestFactory());

            await WaitForReadyAsync(actor);
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(Occupant, state.Occupant);
            Assert.Equal(OccupantType.Human, state.OccupantType);
            Assert.Equal([pending.Id], state.Inbox.Select(item => item.Id));
            Assert.False(channel.WasCalled);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Confirmed_notification_is_not_delivered_again_after_actor_restart()
    {
        var message = Message(
            "bbbbbbbb-1111-2222-3333-444444444444",
            "cccccccc-5555-6666-7777-888888888888");
        var firstChannel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
        var system = CreateActorSystem("human-proxy-terminal-recovery");

        try
        {
            var first = CreatePosition(
                system,
                activeLink: true,
                firstChannel,
                new RecordingRequestFactory());
            await WaitForReadyAsync(first);
            await first.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            await WaitForNotificationStatusAsync(
                first,
                message.Id,
                OccupantNotificationDeliveryStatus.Confirmed);
            await first.GracefulStop(Timeout());

            var recoveredChannel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
            var recovered = CreatePosition(
                system,
                activeLink: true,
                recoveredChannel,
                new RecordingRequestFactory());
            await WaitForReadyAsync(recovered);
            await Task.Delay(250);

            var state = await recovered.Ask<PositionState>(GetPositionState.Instance, Timeout());
            Assert.Equal(
                OccupantNotificationDeliveryStatus.Confirmed,
                state.OccupantNotifications[message.Id].Status);
            Assert.False(recoveredChannel.WasCalled);

            await recovered.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system);
            Assert.Single(events.OfType<OccupantChannelDeliveryRequested>());
            Assert.Single(events.OfType<OccupantChannelDeliveryConfirmed>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Requested_notification_in_snapshot_is_redelivered_and_completed_without_new_request_event()
    {
        var message = Message(
            "dddddddd-1111-2222-3333-444444444444",
            "eeeeeeee-5555-6666-7777-888888888888");
        var requested = new OccupantChannelDeliveryRequested(
            message.Id,
            message.Thread,
            Occupant,
            User,
            Binding,
            At);
        var system = CreateActorSystem("human-proxy-requested-recovery");
        var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());

        try
        {
            await SeedSnapshotAsync(
                system,
                new PositionSnapshot(
                    At,
                    Occupant,
                    OccupantType.Human,
                    inbox: [message],
                    recentHistory: [message.Id],
                    lastConfigurationStamp: Stamp,
                    occupantNotifications: [PersistedOccupantNotification.Requested(requested)]));
            var actor = CreatePosition(
                system,
                activeLink: true,
                channel,
                new RecordingRequestFactory());

            await WaitForReadyAsync(actor);
            await channel.Request.WaitAsync(Timeout());
            var state = await WaitForNotificationStatusAsync(
                actor,
                message.Id,
                OccupantNotificationDeliveryStatus.Confirmed);

            Assert.Equal(1, channel.CallCount);
            Assert.Equal(Binding, state.OccupantNotifications[message.Id].Binding);

            await actor.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system);
            Assert.Empty(events.OfType<OccupantChannelDeliveryRequested>());
            Assert.Single(events.OfType<OccupantChannelDeliveryConfirmed>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Reminder_commands_persist_one_scheduled_and_sent_fact_per_message_and_reminder()
    {
        var message = Message(
            "ffffffff-1111-2222-3333-444444444444",
            "10101010-5555-6666-7777-888888888888");
        var reminder = OccupantReminderId.From(
            Guid.Parse("20202020-aaaa-bbbb-cccc-dddddddddddd"));
        var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
        var system = CreateActorSystem("human-proxy-reminder-state");

        try
        {
            var actor = CreatePosition(
                system,
                activeLink: true,
                channel,
                new RecordingRequestFactory());
            await WaitForReadyAsync(actor);
            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            await WaitForNotificationStatusAsync(
                actor,
                message.Id,
                OccupantNotificationDeliveryStatus.Confirmed);

            var schedule = new ScheduleOccupantReminder(message.Id, reminder, At.AddHours(1));
            actor.Tell(schedule);
            actor.Tell(schedule);
            await WaitForReminderAsync(actor, message.Id, reminder, sent: false);

            var markSent = new MarkOccupantReminderSent(message.Id, reminder, Binding);
            actor.Tell(markSent);
            actor.Tell(markSent);
            var state = await WaitForReminderAsync(actor, message.Id, reminder, sent: true);

            var persistedReminder = Assert.Single(
                state.OccupantNotifications[message.Id].Reminders);
            Assert.Equal(At.AddHours(1), persistedReminder.ScheduledFor);
            Assert.Equal(Binding, persistedReminder.SentBinding);

            await actor.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system);
            Assert.Single(events.OfType<OccupantReminderScheduled>());
            Assert.Single(events.OfType<OccupantReminderSent>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Declared_response_policy_persists_and_delivers_deterministic_reminders()
    {
        var message = DirectiveMessage(
            "30303030-1111-2222-3333-444444444444",
            "40404040-5555-6666-7777-888888888888");
        var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
        var requestFactory = new RecordingRequestFactory();
        var scheduler = new RecordingResponseScheduler();
        var system = CreateActorSystem("human-response-reminder-policy");

        try
        {
            var actor = CreatePolicyPosition(
                system,
                channel,
                requestFactory,
                scheduler,
                new StaticResponseTargetResolver(new PositionEndpointRef(PositionId.From("owner"))),
                new RecordingMessageEmitter(),
                new RecordingKillSwitch());
            await WaitForReadyAsync(actor);
            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            await WaitForNotificationStatusAsync(
                actor,
                message.Id,
                OccupantNotificationDeliveryStatus.Confirmed);

            var trigger = await WaitForScheduledCommandAsync<TriggerOccupantResponseReminder>(
                scheduler,
                command => command.MessageId == message.Id);
            actor.Tell(trigger);
            await WaitForCallCountAsync(channel, 2);
            var state = await WaitForReminderAsync(
                actor,
                message.Id,
                trigger.ReminderId,
                sent: true);

            var notification = state.OccupantNotifications[message.Id];
            Assert.Equal(2, notification.Reminders.Length);
            Assert.All(notification.Reminders, reminder => Assert.NotEqual(default, reminder.ScheduledFor));
            var reminderMessage = Assert.IsType<EventTrigger>(requestFactory.Context!.Message);
            Assert.Equal("occupant-response-reminder", reminderMessage.EventType);
            Assert.Equal(message.Thread, reminderMessage.Thread);
            Assert.Equal(message.Priority, reminderMessage.Priority);
            Assert.Equal(message.Id, requestFactory.Context.CorrelationMessageId);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Response_timeout_persists_valid_escalation_without_removing_original_inbox_item()
    {
        var message = DirectiveMessage(
            "50505050-1111-2222-3333-444444444444",
            "60606060-5555-6666-7777-888888888888");
        var scheduler = new RecordingResponseScheduler();
        var emitter = new RecordingMessageEmitter();
        var system = CreateActorSystem("human-response-timeout-escalation");

        try
        {
            var actor = CreatePolicyPosition(
                system,
                new RecordingChannel(OccupantChannelDeliveryResult.Succeeded()),
                new RecordingRequestFactory(),
                scheduler,
                new StaticResponseTargetResolver(new PositionEndpointRef(PositionId.From("owner"))),
                emitter,
                new RecordingKillSwitch());
            await WaitForReadyAsync(actor);
            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            var timeout = await WaitForScheduledCommandAsync<TriggerOccupantResponseTimeout>(
                scheduler,
                command => command.MessageId == message.Id);

            actor.Tell(timeout);
            var emitted = Assert.IsType<Escalation>(await emitter.Message.Task.WaitAsync(Timeout()));
            var state = await WaitForResponseTimeoutAsync(actor, message.Id);

            Assert.Equal([message.Id], state.Inbox.Select(item => item.Id));
            Assert.NotEqual(message.Id, emitted.Id);
            Assert.Equal(message.Thread, emitted.Thread);
            Assert.Equal(message.Priority, emitted.Priority);
            Assert.Equal(new PositionEndpointRef(PositionId.From("owner")), emitted.To);
            Assert.Equal(emitted, state.OccupantNotifications[message.Id].ResponseTimeout!.Escalation);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Theory]
    [InlineData(Priority.High, false)]
    [InlineData(Priority.Critical, true)]
    public async Task Terminal_timeout_alerts_without_escalation_and_requests_kill_switch_only_for_critical(
        Priority priority,
        bool expectsKillSwitch)
    {
        var message = DirectiveMessage(
            "70707070-1111-2222-3333-444444444444",
            "80808080-5555-6666-7777-888888888888",
            priority);
        var scheduler = new RecordingResponseScheduler();
        var killSwitch = new RecordingKillSwitch();
        var emitter = new RecordingMessageEmitter();
        var system = CreateActorSystem($"human-response-timeout-terminal-{priority}");

        try
        {
            var actor = CreatePolicyPosition(
                system,
                new RecordingChannel(OccupantChannelDeliveryResult.Succeeded()),
                new RecordingRequestFactory(),
                scheduler,
                new StaticResponseTargetResolver(target: null),
                emitter,
                killSwitch);
            await WaitForReadyAsync(actor);
            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            var timeout = await WaitForScheduledCommandAsync<TriggerOccupantResponseTimeout>(
                scheduler,
                command => command.MessageId == message.Id);

            actor.Tell(timeout);
            var state = await WaitForResponseTimeoutAsync(actor, message.Id);
            var handled = state.OccupantNotifications[message.Id].ResponseTimeout!;

            Assert.True(handled.OperationalAlert);
            Assert.Equal(expectsKillSwitch, handled.KillSwitchRequested);
            Assert.Null(handled.Escalation);
            Assert.Equal([message.Id], state.Inbox.Select(item => item.Id));
            Assert.False(emitter.Message.Task.IsCompleted);
            if (expectsKillSwitch)
            {
                var requested = await killSwitch.Completion.Task.WaitAsync(Timeout());
                Assert.Equal(message.Id, requested.SourceMessageId);
            }
            else
            {
                Assert.False(killSwitch.Completion.Task.IsCompleted);
            }
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Correlated_occupant_response_wins_over_an_already_queued_timeout()
    {
        var message = DirectiveMessage(
            "90909090-1111-2222-3333-444444444444",
            "a0a0a0a0-5555-6666-7777-888888888888");
        var scheduler = new RecordingResponseScheduler();
        var emitter = new RecordingMessageEmitter();
        var system = CreateActorSystem("human-response-wins-timeout");

        try
        {
            var actor = CreatePolicyPosition(
                system,
                new RecordingChannel(OccupantChannelDeliveryResult.Succeeded()),
                new RecordingRequestFactory(),
                scheduler,
                new StaticResponseTargetResolver(new PositionEndpointRef(PositionId.From("owner"))),
                emitter,
                new RecordingKillSwitch());
            await WaitForReadyAsync(actor);
            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            var timeout = await WaitForScheduledCommandAsync<TriggerOccupantResponseTimeout>(
                scheduler,
                command => command.MessageId == message.Id);

            var response = await actor.Ask<OccupantReplyEmissionResult>(
                new EmitOccupantReply(
                    message.Id,
                    MessageId.From(Guid.Parse("b0b0b0b0-0000-0000-0000-000000000001")),
                    OccupantReplyAuthor.HumanUser("person-alice", "web-inbox"),
                    "The work has been completed.",
                    ReportKind.Done),
                Timeout());
            actor.Tell(timeout);
            await Task.Delay(100);
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.True(response.IsAccepted);
            Assert.Single(state.OccupantReplies, reply => reply.SourceMessageId == message.Id);
            Assert.Null(state.OccupantNotifications[message.Id].ResponseTimeout);
            Assert.IsType<Report>(await emitter.Message.Task.WaitAsync(Timeout()));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Retain_absence_keeps_the_message_without_channel_or_response_policy_effects()
    {
        var message = DirectiveMessage(
            "d1d1d1d1-1111-2222-3333-444444444444",
            "d2d2d2d2-5555-6666-7777-888888888888");
        var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
        var scheduler = new RecordingResponseScheduler();
        var system = CreateActorSystem("human-absence-retain");

        try
        {
            var actor = CreatePolicyPosition(
                system,
                channel,
                new RecordingRequestFactory(),
                scheduler,
                new StaticResponseTargetResolver(new PositionEndpointRef(PositionId.From("owner"))),
                new RecordingMessageEmitter(),
                new RecordingKillSwitch(),
                OccupantAbsenceAction.Retain);
            await WaitForReadyAsync(actor);

            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            await Task.Delay(100);
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Contains(state.Inbox, item => item.Id == message.Id);
            Assert.False(channel.WasCalled);
            Assert.Empty(state.OccupantNotifications);
            Assert.Empty(state.OccupantAbsenceEscalations);
            Assert.Empty(scheduler.Commands);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Activated_absence_suspends_already_scheduled_reminders_and_timeout()
    {
        var message = DirectiveMessage(
            "d9d9d9d9-1111-2222-3333-444444444444",
            "dadadada-5555-6666-7777-888888888888");
        var availableScheduler = new RecordingResponseScheduler();
        var system = CreateActorSystem("human-absence-suspends-policy");

        try
        {
            var available = CreatePolicyPosition(
                system,
                new RecordingChannel(OccupantChannelDeliveryResult.Succeeded()),
                new RecordingRequestFactory(),
                availableScheduler,
                new StaticResponseTargetResolver(new PositionEndpointRef(PositionId.From("owner"))),
                new RecordingMessageEmitter(),
                new RecordingKillSwitch());
            await WaitForReadyAsync(available);
            await available.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            await WaitForNotificationStatusAsync(
                available,
                message.Id,
                OccupantNotificationDeliveryStatus.Confirmed);
            var reminder = await WaitForScheduledCommandAsync<TriggerOccupantResponseReminder>(
                availableScheduler,
                command => command.MessageId == message.Id);
            var timeout = await WaitForScheduledCommandAsync<TriggerOccupantResponseTimeout>(
                availableScheduler,
                command => command.MessageId == message.Id);
            await available.GracefulStop(Timeout());

            var absentChannel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
            var absentScheduler = new RecordingResponseScheduler();
            var emitter = new RecordingMessageEmitter();
            var absent = CreatePolicyPosition(
                system,
                absentChannel,
                new RecordingRequestFactory(),
                absentScheduler,
                new StaticResponseTargetResolver(new PositionEndpointRef(PositionId.From("owner"))),
                emitter,
                new RecordingKillSwitch(),
                OccupantAbsenceAction.Retain);
            await WaitForReadyAsync(absent);

            absent.Tell(reminder);
            absent.Tell(timeout);
            await Task.Delay(100);
            var state = await absent.Ask<PositionState>(GetPositionState.Instance, Timeout());
            var notification = state.OccupantNotifications[message.Id];

            Assert.False(absentChannel.WasCalled);
            Assert.Empty(absentScheduler.Commands);
            Assert.All(notification.Reminders, persisted => Assert.Null(persisted.SentAt));
            Assert.Null(notification.ResponseTimeout);
            Assert.False(emitter.Message.Task.IsCompleted);
            Assert.Contains(state.Inbox, item => item.Id == message.Id);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Removing_retain_absence_resumes_channel_delivery_for_the_retained_message()
    {
        var message = DirectiveMessage(
            "d7d7d7d7-1111-2222-3333-444444444444",
            "d8d8d8d8-5555-6666-7777-888888888888");
        var system = CreateActorSystem("human-absence-removed");

        try
        {
            var absent = CreatePolicyPosition(
                system,
                new RecordingChannel(OccupantChannelDeliveryResult.Succeeded()),
                new RecordingRequestFactory(),
                new RecordingResponseScheduler(),
                new StaticResponseTargetResolver(new PositionEndpointRef(PositionId.From("owner"))),
                new RecordingMessageEmitter(),
                new RecordingKillSwitch(),
                OccupantAbsenceAction.Retain);
            await WaitForReadyAsync(absent);
            await absent.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            await absent.GracefulStop(Timeout());

            var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
            var available = CreatePolicyPosition(
                system,
                channel,
                new RecordingRequestFactory(),
                new RecordingResponseScheduler(),
                new StaticResponseTargetResolver(new PositionEndpointRef(PositionId.From("owner"))),
                new RecordingMessageEmitter(),
                new RecordingKillSwitch());
            await WaitForReadyAsync(available);
            var request = await channel.Request.WaitAsync(Timeout());
            var state = await WaitForNotificationStatusAsync(
                available,
                message.Id,
                OccupantNotificationDeliveryStatus.Confirmed);

            Assert.Equal(message.Id, request.MessageId);
            Assert.Equal(1, channel.CallCount);
            Assert.Contains(state.Inbox, item => item.Id == message.Id);
            await available.GracefulStop(Timeout());

            var events = await ReadEventsAsync(system);
            Assert.Single(events.OfType<OccupantChannelDeliveryRequested>());
            Assert.Single(events.OfType<OccupantChannelDeliveryConfirmed>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Escalate_absence_persists_one_validated_escalation_and_reemits_it_after_restart()
    {
        var message = DirectiveMessage(
            "d3d3d3d3-1111-2222-3333-444444444444",
            "d4d4d4d4-5555-6666-7777-888888888888");
        var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
        var emitter = new RecordingMessageEmitter();
        var system = CreateActorSystem("human-absence-escalate");

        try
        {
            var actor = CreatePolicyPosition(
                system,
                channel,
                new RecordingRequestFactory(),
                new RecordingResponseScheduler(),
                new StaticResponseTargetResolver(new PositionEndpointRef(PositionId.From("owner"))),
                emitter,
                new RecordingKillSwitch(),
                OccupantAbsenceAction.Escalate);
            await WaitForReadyAsync(actor);

            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            var emitted = Assert.IsType<Escalation>(await emitter.Message.Task.WaitAsync(Timeout()));
            var state = await WaitForAbsenceEscalationAsync(actor, message.Id);

            Assert.False(channel.WasCalled);
            Assert.Empty(state.OccupantNotifications);
            Assert.Contains(state.Inbox, item => item.Id == message.Id);
            Assert.Equal(message.Thread, emitted.Thread);
            Assert.Equal(message.Priority, emitted.Priority);
            Assert.Equal(new PositionEndpointRef(PositionId.From("owner")), emitted.To);
            Assert.Equal(
                OccupantAbsenceEscalationIdentity.For(Entity, message.Id),
                emitted.Id);
            Assert.Equal(emitted, state.OccupantAbsenceEscalations[message.Id].Escalation);

            actor.Tell(new InitializeOccupantAbsenceEscalation(message.Id));
            await Task.Delay(100);
            await actor.GracefulStop(Timeout());

            var recoveredEmitter = new RecordingMessageEmitter();
            var recovered = CreatePolicyPosition(
                system,
                new RecordingChannel(OccupantChannelDeliveryResult.Succeeded()),
                new RecordingRequestFactory(),
                new RecordingResponseScheduler(),
                new StaticResponseTargetResolver(new PositionEndpointRef(PositionId.From("owner"))),
                recoveredEmitter,
                new RecordingKillSwitch(),
                OccupantAbsenceAction.Escalate);
            await WaitForReadyAsync(recovered);
            var recoveredEscalation = await recoveredEmitter.Message.Task.WaitAsync(Timeout());
            Assert.Equal(emitted, recoveredEscalation);
            await recovered.GracefulStop(Timeout());

            var events = await ReadEventsAsync(system);
            Assert.Single(events.OfType<OccupantAbsenceEscalationHandled>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Theory]
    [InlineData(Priority.High, false)]
    [InlineData(Priority.Critical, true)]
    public async Task Absence_without_target_alerts_and_requests_kill_switch_only_for_critical(
        Priority priority,
        bool expectsKillSwitch)
    {
        var message = DirectiveMessage(
            "d5d5d5d5-1111-2222-3333-444444444444",
            "d6d6d6d6-5555-6666-7777-888888888888",
            priority);
        var killSwitch = new RecordingKillSwitch();
        var emitter = new RecordingMessageEmitter();
        var system = CreateActorSystem($"human-absence-terminal-{priority}");

        try
        {
            var actor = CreatePolicyPosition(
                system,
                new RecordingChannel(OccupantChannelDeliveryResult.Succeeded()),
                new RecordingRequestFactory(),
                new RecordingResponseScheduler(),
                new StaticResponseTargetResolver(target: null),
                emitter,
                killSwitch,
                OccupantAbsenceAction.Escalate);
            await WaitForReadyAsync(actor);

            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            var state = await WaitForAbsenceEscalationAsync(actor, message.Id);
            var handled = state.OccupantAbsenceEscalations[message.Id];

            Assert.True(handled.OperationalAlert);
            Assert.Equal(expectsKillSwitch, handled.KillSwitchRequested);
            Assert.Null(handled.Escalation);
            Assert.Contains(state.Inbox, item => item.Id == message.Id);
            Assert.False(emitter.Message.Task.IsCompleted);
            if (expectsKillSwitch)
            {
                var requested = await killSwitch.Completion.Task.WaitAsync(Timeout());
                Assert.Equal(message.Id, requested.SourceMessageId);
            }
            else
            {
                Assert.False(killSwitch.Completion.Task.IsCompleted);
            }
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public void Human_proxy_has_no_recoverable_actor_state()
    {
        Assert.False(typeof(ReceivePersistentActor).IsAssignableFrom(typeof(HumanProxyActor)));
        Assert.All(
            typeof(HumanProxyActor).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly),
            field => Assert.True(field.IsInitOnly));
    }

    private static IActorRef CreatePosition(
        ActorSystem system,
        bool activeLink,
        IOccupantChannel channel,
        IOccupantChannelDeliveryRequestFactory requestFactory) =>
        system.ActorOf(
            Props.Create(() => new PositionActor(
                Entity.Value,
                new StaticConfigurationProvider(RuntimeConfiguration(activeLink, false, null)),
                new PositionOccupantFactory(channel, requestFactory),
                () => At)),
            $"position-{Guid.NewGuid():N}");

    private static IActorRef CreatePolicyPosition(
        ActorSystem system,
        IOccupantChannel channel,
        IOccupantChannelDeliveryRequestFactory requestFactory,
        IOccupantResponseScheduler scheduler,
        IOccupantResponseEscalationTargetResolver targetResolver,
        IPositionMessageEmitter emitter,
        IOccupantResponseKillSwitch killSwitch,
        OccupantAbsenceAction? absenceAction = null) =>
        system.ActorOf(
            Props.Create(() => new PositionActor(
                Entity.Value,
                new StaticConfigurationProvider(RuntimeConfiguration(true, true, absenceAction)),
                new PositionOccupantFactory(channel, requestFactory),
                null,
                () => At,
                null,
                AllowingReplyValidator.Instance,
                emitter,
                scheduler,
                targetResolver,
                killSwitch)),
            $"position-{Guid.NewGuid():N}");

    private static PositionRuntimeConfiguration RuntimeConfiguration(
        bool activeLink,
        bool withPolicy = false,
        OccupantAbsenceAction? absenceAction = null) =>
        new(
            Stamp,
            Entity.Organization,
            Entity.Position,
            new PositionRuntimeDescriptor(
                UnitId.From("delivery"),
                reportsTo: PositionId.From("owner"),
                name: "Delivery lead",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.Human,
                configuredIdentity: Occupant,
                humanIdentity: activeLink
                    ? new HumanOccupantRuntimeIdentity(User, Binding)
                    : null,
                responsePolicy: withPolicy
                    ? new OccupantResponsePolicyRuntimeConfiguration(
                        2,
                        TimeSpan.FromHours(4),
                        TimeSpan.FromHours(16),
                        "Europe/Lisbon",
                        new TimeOnly(9, 0),
                        new TimeOnly(18, 0))
                    : null,
                absence: absenceAction is { } action
                    ? new OccupantAbsenceConfiguration(action)
                    : null),
            new PositionAuthorityRuntimeConfiguration(Array.Empty<string>()));

    private static Memo Message(string messageId, string threadId) =>
        new(
            Hive.Domain.Identity.MessageId.From(Guid.Parse(messageId)),
            Entity.Organization,
            new PositionEndpointRef(PositionId.From("owner")),
            new PositionEndpointRef(Entity.Position),
            Hive.Domain.Identity.ThreadId.From(Guid.Parse(threadId)),
            Priority.Normal,
            schemaVersion: 1,
            At,
            deadline: null,
            body: "A delivery decision needs human attention.");

    private static Hive.Domain.Messaging.Directive DirectiveMessage(
        string messageId,
        string threadId,
        Priority priority = Priority.High) =>
        new(
            Hive.Domain.Identity.MessageId.From(Guid.Parse(messageId)),
            Entity.Organization,
            new PositionEndpointRef(PositionId.From("owner")),
            new PositionEndpointRef(Entity.Position),
            Hive.Domain.Identity.ThreadId.From(Guid.Parse(threadId)),
            priority,
            schemaVersion: 1,
            At,
            deadline: null,
            DirectiveId.New(),
            parentDirectiveId: null,
            objective: "Make the delivery decision.",
            context: "The request requires a human response.");

    private static ActorSystem CreateActorSystem(string prefix) =>
        ActorSystem.Create(
            $"{prefix}-{Guid.NewGuid():N}",
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

        throw new TimeoutException("PositionActor did not reach Ready.");
    }

    private static async Task<PositionState> WaitForNotificationStatusAsync(
        IActorRef actor,
        MessageId message,
        OccupantNotificationDeliveryStatus status)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, TimeSpan.FromSeconds(1));
            if (state.OccupantNotifications.TryGetValue(message, out var notification) &&
                notification.Status == status)
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Notification '{message}' did not reach '{status}'.");
    }

    private static async Task<PositionState> WaitForReminderAsync(
        IActorRef actor,
        MessageId message,
        OccupantReminderId reminder,
        bool sent)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, TimeSpan.FromSeconds(1));
            if (state.OccupantNotifications.TryGetValue(message, out var notification) &&
                notification.Reminders.Any(item =>
                    item.Id == reminder && (item.SentAt is not null) == sent))
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Reminder '{reminder}' did not reach sent={sent}.");
    }

    private static async Task<T> WaitForScheduledCommandAsync<T>(
        RecordingResponseScheduler scheduler,
        Func<T, bool> predicate)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var command = scheduler.Commands.OfType<T>().FirstOrDefault(predicate);
            if (command is not null)
            {
                return command;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Scheduled command '{typeof(T).Name}' was not observed.");
    }

    private static async Task WaitForCallCountAsync(RecordingChannel channel, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (channel.CallCount >= expected)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Occupant channel did not reach {expected} calls.");
    }

    private static async Task<PositionState> WaitForResponseTimeoutAsync(
        IActorRef actor,
        MessageId messageId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, TimeSpan.FromSeconds(1));
            if (state.OccupantNotifications.TryGetValue(messageId, out var notification)
                && notification.ResponseTimeout is not null)
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Response timeout for '{messageId}' was not persisted.");
    }

    private static async Task<PositionState> WaitForAbsenceEscalationAsync(
        IActorRef actor,
        MessageId messageId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, TimeSpan.FromSeconds(1));
            if (state.OccupantAbsenceEscalations.ContainsKey(messageId))
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Absence escalation for '{messageId}' was not persisted.");
    }

    private static async Task SeedSnapshotAsync(ActorSystem system, PositionSnapshot snapshot)
    {
        var seeder = system.ActorOf(
            Props.Create(() => new PersistenceProbe(PositionActor.PersistenceIdFor(Entity.Value))),
            $"seed-{Guid.NewGuid():N}");
        await seeder.Ask<SnapshotSeeded>(new SeedSnapshot(snapshot), Timeout());
        await seeder.GracefulStop(Timeout());
    }

    private static async Task<IReadOnlyList<PositionEvent>> ReadEventsAsync(ActorSystem system)
    {
        var reader = system.ActorOf(
            Props.Create(() => new PersistenceProbe(PositionActor.PersistenceIdFor(Entity.Value))),
            $"reader-{Guid.NewGuid():N}");
        var events = await reader.Ask<IReadOnlyList<PositionEvent>>(ReadEvents.Instance, Timeout());
        await reader.GracefulStop(Timeout());
        return events;
    }

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticConfigurationProvider(PositionRuntimeConfiguration configuration)
        : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PositionRuntimeConfigurationLoadResult.Loaded(configuration));
    }

    private sealed class RecordingChannel(OccupantChannelDeliveryResult result) : IOccupantChannel
    {
        private readonly TaskCompletionSource<OccupantChannelDeliveryRequest> _request =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OccupantChannelDeliveryRequest> Request => _request.Task;

        public bool WasCalled => _request.Task.IsCompleted;

        public int CallCount { get; private set; }

        public Task<OccupantChannelDeliveryResult> DeliverAsync(
            OccupantChannelDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            _request.TrySetResult(request);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingRequestFactory : IOccupantChannelDeliveryRequestFactory
    {
        public OccupantChannelDeliveryContext? Context { get; private set; }

        public OccupantChannelDeliveryRequest Create(OccupantChannelDeliveryContext context)
        {
            Context = context;
            return new OccupantChannelDeliveryRequest(
                context.OrganizationId,
                context.PositionId,
                context.OccupantId,
                context.UserId,
                context.OccupantChannelBindingId!,
                context.Message.Id,
                context.Message.Thread,
                "A message is waiting in your HIVE inbox.",
                OccupantChannelCorrelationToken.From("opaque.test.token"));
        }
    }

    private sealed class RecordingResponseScheduler : IOccupantResponseScheduler
    {
        public ConcurrentQueue<object> Commands { get; } = new();

        public void Schedule(IActorContext context, IActorRef receiver, object command, TimeSpan delay) =>
            Commands.Enqueue(command);
    }

    private sealed class StaticResponseTargetResolver(EndpointRef? target)
        : IOccupantResponseEscalationTargetResolver
    {
        public ValueTask<EndpointRef?> ResolveAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(target);
    }

    private sealed class AllowingReplyValidator : IOccupantReplyMessageValidator
    {
        public static AllowingReplyValidator Instance { get; } = new();

        public ValueTask<ValidationResult> ValidateAsync(
            PositionState state,
            OrgMessage message,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ValidationResult.Valid);
    }

    private sealed class RecordingMessageEmitter : IPositionMessageEmitter
    {
        public TaskCompletionSource<OrgMessage> Message { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(ActorSystem system, OrgMessage message) => Message.TrySetResult(message);
    }

    private sealed class RecordingKillSwitch : IOccupantResponseKillSwitch
    {
        public TaskCompletionSource<OccupantResponseKillSwitchRequest> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Request(ActorSystem system, OccupantResponseKillSwitchRequest request) =>
            Completion.TrySetResult(request);
    }

    private sealed class DeliveryReportObserver : ReceiveActor
    {
        public DeliveryReportObserver(
            TaskCompletionSource<PositionOccupantChannelDeliveryReported> completion)
        {
            Receive<PositionOccupantChannelDeliveryReported>(completion.TrySetResult);
        }
    }

    private sealed class PersistenceProbe : ReceivePersistentActor
    {
        private readonly List<PositionEvent> _events = [];
        private IActorRef? _snapshotReplyTo;

        public PersistenceProbe(string persistenceId)
        {
            PersistenceId = persistenceId;
            Recover<PositionEvent>(_events.Add);
            RecoverAny(_ => { });
            Command<SeedSnapshot>(command =>
            {
                _snapshotReplyTo = Sender;
                SaveSnapshot(command.Snapshot);
            });
            Command<SaveSnapshotSuccess>(_ =>
            {
                _snapshotReplyTo?.Tell(SnapshotSeeded.Instance);
                _snapshotReplyTo = null;
            });
            Command<SaveSnapshotFailure>(failure =>
            {
                _snapshotReplyTo?.Tell(new Status.Failure(failure.Cause));
                _snapshotReplyTo = null;
            });
            Command<ReadEvents>(_ => Sender.Tell(_events.ToArray()));
        }

        public override string PersistenceId { get; }
    }

    private sealed record SeedSnapshot(PositionSnapshot Snapshot);

    private sealed record SnapshotSeeded
    {
        public static SnapshotSeeded Instance { get; } = new();
    }

    private sealed record ReadEvents
    {
        public static ReadEvents Instance { get; } = new();
    }
}
