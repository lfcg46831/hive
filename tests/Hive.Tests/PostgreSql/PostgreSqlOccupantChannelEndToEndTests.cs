using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.OccupantChannels;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.OccupantChannels;
using Hive.Infrastructure.OccupantChannels.PostgreSql;

namespace Hive.Tests.PostgreSql;

[Collection(AkkaPostgreSqlCollection.Name)]
public sealed class PostgreSqlOccupantChannelEndToEndTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task Human_position_without_active_link_keeps_inbox_without_materializing_proxy()
    {
        await using var fixture = await OccupantChannelIntegrationFixture.StartAsync(
            postgres,
            activeLink: false);
        var message = OccupantChannelIntegrationFixture.Directive(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("10000000-0000-0000-0000-000000000002"));

        var result = await fixture.AcceptAsync(message);
        var state = await fixture.StateAsync();

        Assert.Equal(AcceptMessageDecision.Accepted, result.Decision);
        Assert.Null(state.Occupant);
        Assert.Null(state.OccupantType);
        Assert.Equal([message.Id], state.Inbox.Select(item => item.Id));
        Assert.Empty(state.OccupantNotifications);
        Assert.Empty(fixture.Transport.Messages);
        Assert.False(await fixture.HasHumanProxyAsync());
        Assert.DoesNotContain(
            fixture.Projections.Events.OfType<PositionEventCommitted>(),
            committed => committed.Event is OccupantChanged or MessageDispatched);
    }

    [Fact]
    public async Task Outbound_delivery_resolves_binding_without_persisting_endpoint_and_dedupes_redelivery()
    {
        await using var fixture = await OccupantChannelIntegrationFixture.StartAsync(postgres);
        var message = OccupantChannelIntegrationFixture.Directive(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Guid.Parse("20000000-0000-0000-0000-000000000002"));

        Assert.Equal(AcceptMessageDecision.Accepted, (await fixture.AcceptAsync(message)).Decision);
        var email = await fixture.WaitForEmailAsync(message.Id);
        var state = await fixture.WaitForNotificationAsync(
            message.Id,
            OccupantNotificationDeliveryStatus.Confirmed);

        Assert.Equal(OccupantChannelIntegrationFixture.Endpoint, email.Recipient);
        Assert.True(await fixture.HasHumanProxyAsync());
        var query = Assert.Single(fixture.Bindings.OutboundQueries);
        Assert.Equal(OccupantChannelIntegrationFixture.Entity.Organization, query.OrganizationId);
        Assert.Equal(OccupantChannelIntegrationFixture.Entity.Position, query.PositionId);
        Assert.Equal(OccupantChannelIntegrationFixture.Occupant, query.OccupantId);
        Assert.Equal(OccupantChannelIntegrationFixture.User, query.UserId);
        Assert.Equal(OccupantChannelIntegrationFixture.Binding, query.BindingId);
        Assert.Equal(
            OccupantChannelIntegrationFixture.Binding,
            state.OccupantNotifications[message.Id].Binding);
        Assert.DoesNotContain(
            OccupantChannelIntegrationFixture.Endpoint,
            string.Join(' ', state.OccupantNotifications.Values),
            StringComparison.OrdinalIgnoreCase);
        await fixture.AssertEndpointAbsentFromPersistenceAsync();

        await fixture.RestartPositionAsync();
        var redelivery = await fixture.AcceptAsync(message);
        await Task.Delay(150);

        Assert.Equal(AcceptMessageDecision.AlreadyAccepted, redelivery.Decision);
        Assert.Single(fixture.Transport.Messages);
        Assert.Single(fixture.Bindings.OutboundQueries);
        Assert.Equal(
            OccupantNotificationDeliveryStatus.Confirmed,
            (await fixture.StateAsync()).OccupantNotifications[message.Id].Status);
        await fixture.AssertEndpointAbsentFromPersistenceAsync();
    }

    [Fact]
    public async Task Revoked_binding_blocks_new_outbound_delivery_and_correlated_inbound_reply()
    {
        await using var fixture = await OccupantChannelIntegrationFixture.StartAsync(postgres);
        var delivered = OccupantChannelIntegrationFixture.Directive(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000002"));
        await fixture.AcceptAsync(delivered);
        await fixture.WaitForNotificationAsync(
            delivered.Id,
            OccupantNotificationDeliveryStatus.Confirmed);

        fixture.Bindings.Revoke();
        var blocked = OccupantChannelIntegrationFixture.Directive(
            Guid.Parse("30000000-0000-0000-0000-000000000003"),
            Guid.Parse("30000000-0000-0000-0000-000000000004"));
        await fixture.AcceptAsync(blocked);
        var state = await fixture.WaitForNotificationAsync(
            blocked.Id,
            OccupantNotificationDeliveryStatus.Failed);

        Assert.Equal(
            OccupantChannelDeliveryErrorCode.BindingRevoked,
            state.OccupantNotifications[blocked.Id].Failure!.Code);
        Assert.Single(fixture.Transport.Messages);

        var tokens = fixture.CreateTokenService();
        var token = tokens.Issue(new OccupantChannelCorrelationTokenRequest(
            blocked.OrganizationId,
            OccupantChannelIntegrationFixture.HumanPosition,
            blocked.Id,
            blocked.Thread));
        var parse = await fixture.CreateInboundParser(tokens).ParseAsync(
            new ImapInboundEmailEnvelope(
                "occupant-replies",
                "INBOX",
                7,
                1,
                OccupantChannelIntegrationFixture.ReplyMessage(
                    OccupantChannelIntegrationFixture.Endpoint,
                    $"Completed\nHIVE-Occupant-Correlation: {token.Value}"),
                fixture.Clock.GetUtcNow()));

        Assert.Equal(InboundOccupantEmailParseStatus.Rejected, parse.Status);
        Assert.Equal(InboundOccupantEmailFailureCode.BindingRevoked, parse.Failure);
    }

    [Fact]
    public async Task Correlated_email_reply_crosses_postgresql_staging_and_becomes_canonical_report()
    {
        await using var fixture = await OccupantChannelIntegrationFixture.StartAsync(postgres);
        var source = OccupantChannelIntegrationFixture.Directive(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            Guid.Parse("40000000-0000-0000-0000-000000000002"));
        await fixture.AcceptAsync(source);
        var email = await fixture.WaitForEmailAsync(source.Id);
        var token = CorrelationToken(email.PlainTextBody);
        await using var store = fixture.CreateInboundStore();
        await StageAsync(
            store,
            uid: 1,
            OccupantChannelIntegrationFixture.ReplyMessage(
                OccupantChannelIntegrationFixture.Endpoint,
                $"Work is progressing.\nHIVE-Occupant-Correlation: {token}"),
            fixture.Clock.GetUtcNow());

        var admission = await fixture.CreateInboundProcessor(
            store,
            fixture.CreateInboundParser(fixture.CreateTokenService())).ProcessPendingAsync();
        var emission = await fixture.CreateReplyProcessor(store).ProcessAcceptedAsync();
        var report = await fixture.Emitter.WaitForAsync<Report>(
            candidate => candidate.Thread == source.Thread);
        var state = await fixture.StateAsync();

        Assert.Equal(new InboundOccupantEmailProcessingResult(1, 1, 0, 0, 0), admission);
        Assert.Equal(new InboundOccupantEmailReplyProcessingResult(1, 1, 0, 0, 0), emission);
        Assert.Equal(source.Thread, report.Thread);
        Assert.Equal(source.DirectiveId, report.AboutDirectiveId);
        Assert.Equal(ReportKind.Progress, report.Kind);
        Assert.Equal("Work is progressing.", report.Body);
        Assert.Equal(
            new PositionEndpointRef(OccupantChannelIntegrationFixture.HumanPosition),
            report.From);
        Assert.Equal(
            new PositionEndpointRef(OccupantChannelIntegrationFixture.SuperiorPosition),
            report.To);
        var persisted = Assert.Single(
            state.OccupantReplies,
            reply => reply.SourceMessageId == source.Id);
        Assert.Equal(report, persisted.Message);
        Assert.Equal("email", persisted.Author.Channel);
        Assert.Empty(await store.ReadAcceptedWorkRepliesAsync(
            "occupant-replies",
            "INBOX",
            10));
    }

    [Fact]
    public async Task Email_approval_is_accepted_and_sender_reuse_and_expiry_are_rejected()
    {
        await using var fixture = await OccupantChannelIntegrationFixture.StartAsync(postgres);
        await using var store = fixture.CreateInboundStore();
        var acceptedRequest = OccupantChannelIntegrationFixture.ApprovalRequest(
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            Guid.Parse("50000000-0000-0000-0000-000000000002"));
        await fixture.AcceptAsync(acceptedRequest);
        var acceptedToken = CorrelationToken(
            (await fixture.WaitForEmailAsync(acceptedRequest.Id)).PlainTextBody);
        await StageAsync(
            store,
            uid: 1,
            OccupantChannelIntegrationFixture.ReplyMessage(
                OccupantChannelIntegrationFixture.Endpoint,
                $"APPROVE\nRelease checks passed.\nHIVE-Occupant-Correlation: {acceptedToken}"),
            fixture.Clock.GetUtcNow());

        var parser = fixture.CreateInboundParser(fixture.CreateTokenService());
        Assert.Equal(
            new InboundOccupantEmailProcessingResult(1, 1, 0, 0, 0),
            await fixture.CreateInboundProcessor(store, parser).ProcessPendingAsync());
        Assert.Equal(
            new InboundOccupantEmailDecisionProcessingResult(1, 1, 0, 0, 0),
            await fixture.CreateDecisionProcessor(store).ProcessAcceptedAsync());
        var decision = await fixture.Emitter.WaitForAsync<ApprovalDecision>(
            candidate => candidate.RequestId == acceptedRequest.Id);

        Assert.True(decision.Approved);
        Assert.Equal("Release checks passed.", decision.Reason);
        Assert.Contains(
            (await fixture.StateAsync()).OccupantReplies,
            reply => reply.SourceMessageId == acceptedRequest.Id
                && reply.Message == decision
                && reply.Author.Channel == "email");

        await StageAsync(
            store,
            uid: 2,
            OccupantChannelIntegrationFixture.ReplyMessage(
                "attacker@example.test",
                $"APPROVE\nHIVE-Occupant-Correlation: {acceptedToken}"),
            fixture.Clock.GetUtcNow());
        var divergent = await fixture.CreateInboundProcessor(store, parser).ProcessPendingAsync();
        Assert.Equal(new InboundOccupantEmailProcessingResult(1, 0, 1, 0, 0), divergent);
        Assert.Equal(
            "sender-mismatch",
            await fixture.ReadInboundRejectionCodeAsync(uid: 2));

        await StageAsync(
            store,
            uid: 3,
            OccupantChannelIntegrationFixture.ReplyMessage(
                OccupantChannelIntegrationFixture.Endpoint,
                $"REJECT\nHIVE-Occupant-Correlation: {acceptedToken}"),
            fixture.Clock.GetUtcNow());
        var reused = await fixture.CreateInboundProcessor(store, parser).ProcessPendingAsync();
        Assert.Equal(new InboundOccupantEmailProcessingResult(1, 0, 1, 0, 0), reused);
        Assert.Equal(
            "decision-token-already-used",
            await fixture.ReadInboundRejectionCodeAsync(uid: 3));

        var expiringRequest = OccupantChannelIntegrationFixture.ApprovalRequest(
            Guid.Parse("50000000-0000-0000-0000-000000000004"),
            Guid.Parse("50000000-0000-0000-0000-000000000005"));
        await fixture.AcceptAsync(expiringRequest);
        var expiringToken = CorrelationToken(
            (await fixture.WaitForEmailAsync(expiringRequest.Id)).PlainTextBody);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(1);
        await StageAsync(
            store,
            uid: 4,
            OccupantChannelIntegrationFixture.ReplyMessage(
                OccupantChannelIntegrationFixture.Endpoint,
                $"APPROVE\nHIVE-Occupant-Correlation: {expiringToken}"),
            fixture.Clock.GetUtcNow());
        var expired = await fixture.CreateInboundProcessor(store, parser).ProcessPendingAsync();

        Assert.Equal(new InboundOccupantEmailProcessingResult(1, 0, 1, 0, 0), expired);
        Assert.Equal("token-expired", await fixture.ReadInboundRejectionCodeAsync(uid: 4));
    }

    [Fact]
    public async Task Reminder_and_timeout_use_valid_vertical_escalation_and_keep_original_pending()
    {
        await using var fixture = await OccupantChannelIntegrationFixture.StartAsync(
            postgres,
            responsePolicy: true);
        var source = OccupantChannelIntegrationFixture.Directive(
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            Guid.Parse("60000000-0000-0000-0000-000000000002"));
        await fixture.AcceptAsync(source);
        await fixture.WaitForNotificationAsync(
            source.Id,
            OccupantNotificationDeliveryStatus.Confirmed);
        var reminder = await fixture.Scheduler.WaitForAsync<TriggerOccupantResponseReminder>(
            command => command.MessageId == source.Id);
        var timeout = await fixture.Scheduler.WaitForAsync<TriggerOccupantResponseTimeout>(
            command => command.MessageId == source.Id);

        fixture.Position.Tell(reminder);
        await WaitForReminderSentAsync(fixture, source.Id, reminder.ReminderId);
        fixture.Position.Tell(timeout);
        var escalation = await fixture.Emitter.WaitForAsync<Escalation>(
            candidate => candidate.Thread == source.Thread);
        var state = await fixture.WaitForTimeoutAsync(source.Id);

        Assert.Equal(
            new PositionEndpointRef(OccupantChannelIntegrationFixture.SuperiorPosition),
            escalation.To);
        Assert.Equal(source.Thread, escalation.Thread);
        Assert.Equal(source.Priority, escalation.Priority);
        Assert.Contains(state.Inbox, message => message.Id == source.Id);
        Assert.Equal(
            escalation,
            state.OccupantNotifications[source.Id].ResponseTimeout!.Escalation);
        Assert.Equal(2, fixture.Transport.Messages.Count);
        Assert.Contains(
            fixture.Projections.Events.OfType<PositionEventCommitted>(),
            committed => committed.Event is OccupantReminderSent);
        Assert.Contains(
            fixture.Projections.Events.OfType<PositionEventCommitted>(),
            committed => committed.Event is OccupantResponseTimeoutHandled handled
                && handled.Escalation == escalation);
    }

    [Fact]
    public async Task Terminal_timeout_records_operational_alert_without_invalid_owner_message()
    {
        await using var fixture = await OccupantChannelIntegrationFixture.StartAsync(
            postgres,
            responsePolicy: true,
            terminalEscalationTarget: true);
        var source = OccupantChannelIntegrationFixture.Directive(
            Guid.Parse("70000000-0000-0000-0000-000000000001"),
            Guid.Parse("70000000-0000-0000-0000-000000000002"));
        await fixture.AcceptAsync(source);
        var timeout = await fixture.Scheduler.WaitForAsync<TriggerOccupantResponseTimeout>(
            command => command.MessageId == source.Id);

        fixture.Position.Tell(timeout);
        var state = await fixture.WaitForTimeoutAsync(source.Id);
        var handled = state.OccupantNotifications[source.Id].ResponseTimeout!;

        Assert.True(handled.OperationalAlert);
        Assert.False(handled.KillSwitchRequested);
        Assert.Null(handled.Escalation);
        Assert.Contains(state.Inbox, message => message.Id == source.Id);
        Assert.Empty(fixture.Emitter.Messages);
        Assert.Contains(
            fixture.Projections.Events.OfType<PositionEventCommitted>(),
            committed => committed.Event == handled);
    }

    [Theory]
    [InlineData(OccupantAbsenceAction.Retain, false)]
    [InlineData(OccupantAbsenceAction.Escalate, true)]
    public async Task Active_absence_suppresses_channel_and_applies_configured_action(
        OccupantAbsenceAction action,
        bool expectsEscalation)
    {
        await using var fixture = await OccupantChannelIntegrationFixture.StartAsync(
            postgres,
            responsePolicy: true,
            absenceAction: action);
        var source = OccupantChannelIntegrationFixture.Directive(
            action == OccupantAbsenceAction.Retain
                ? Guid.Parse("80000000-0000-0000-0000-000000000001")
                : Guid.Parse("80000000-0000-0000-0000-000000000003"),
            action == OccupantAbsenceAction.Retain
                ? Guid.Parse("80000000-0000-0000-0000-000000000002")
                : Guid.Parse("80000000-0000-0000-0000-000000000004"));
        await fixture.AcceptAsync(source);

        var state = expectsEscalation
            ? await fixture.WaitForAbsenceAsync(source.Id)
            : await fixture.StateAsync();
        Assert.Contains(state.Inbox, message => message.Id == source.Id);
        Assert.Empty(state.OccupantNotifications);
        Assert.Empty(fixture.Transport.Messages);
        Assert.Empty(fixture.Scheduler.Commands);

        if (expectsEscalation)
        {
            var escalation = await fixture.Emitter.WaitForAsync<Escalation>();
            Assert.Equal(
                new PositionEndpointRef(OccupantChannelIntegrationFixture.SuperiorPosition),
                escalation.To);
            Assert.Equal(
                escalation,
                state.OccupantAbsenceEscalations[source.Id].Escalation);
        }
        else
        {
            Assert.Empty(state.OccupantAbsenceEscalations);
            Assert.Empty(fixture.Emitter.Messages);
        }
    }

    private static async Task StageAsync(
        PostgreSqlImapInboundEmailStore store,
        uint uid,
        byte[] rawMessage,
        DateTimeOffset at)
    {
        var checkpoint = await store.ReadCheckpointAsync("occupant-replies", "INBOX");
        var committed = await store.CommitBatchAsync(
            checkpoint,
            new ImapInboundEmailBatch(
                "occupant-replies",
                "INBOX",
                7,
                uid,
                [new FetchedImapMessage(uid, rawMessage)]),
            at);
        Assert.True(committed.IsApplied);
        Assert.Equal(1, committed.InsertedCount);
    }

    private static string CorrelationToken(string body)
    {
        const string marker = "HIVE-Occupant-Correlation:";
        var line = body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Single(candidate => candidate.TrimStart().StartsWith(marker, StringComparison.Ordinal));
        return line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();
    }

    private static async Task WaitForReminderSentAsync(
        OccupantChannelIntegrationFixture fixture,
        MessageId messageId,
        OccupantReminderId reminderId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await fixture.StateAsync();
            if (state.OccupantNotifications.TryGetValue(messageId, out var notification)
                && notification.Reminders.Any(reminder =>
                    reminder.Id == reminderId && reminder.SentAt is not null))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Reminder '{reminderId}' was not sent.");
    }
}
