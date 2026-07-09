using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Actors.Sharding;
using Hive.Api.Directives;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Positions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class DirectiveSubmissionEndpointTests
{
    private static readonly DateTimeOffset SentAt =
        new(2026, 7, 7, 10, 15, 30, TimeSpan.Zero);

    [Fact]
    public async Task Submit_root_directive_accepts_canonical_payload_without_case_specific_surface()
    {
        var sink = new CapturingDirectiveSubmissionSink();
        await using var app = BuildApp(sink);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            new
            {
                messageId = "aaaaaaaa-0000-0000-0000-000000000010",
                from = new { kind = "position", positionId = "ceo" },
                to = new { kind = "position", positionId = "delivery-lead" },
                threadId = "aaaaaaaa-0000-0000-0000-000000000011",
                priority = "high",
                schemaVersion = 1,
                sentAt = "2026-07-07T10:15:30Z",
                directiveId = "aaaaaaaa-0000-0000-0000-000000000012",
                objective = "Review the incoming production request and report the conclusion.",
                context = "Free-form producer context. Completion criteria: severity, missing information, and next action.",
            });

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("accepted", json.GetProperty("status").GetString());
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000010", json.GetProperty("messageId").GetString());
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000012", json.GetProperty("directiveId").GetString());
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000011", json.GetProperty("threadId").GetString());
        Assert.Equal("delivery-lead", json.GetProperty("toPositionId").GetString());

        var directive = Assert.IsType<Directive>(sink.Submitted);
        Assert.Equal("acme-delivery", directive.OrganizationId.Value);
        Assert.Equal("ceo", Assert.IsType<PositionEndpointRef>(directive.From).PositionId.Value);
        Assert.Equal("delivery-lead", Assert.IsType<PositionEndpointRef>(directive.To).PositionId.Value);
        Assert.Equal(Priority.High, directive.Priority);
        Assert.Equal(1, directive.SchemaVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T10:15:30Z"), directive.SentAt);
        Assert.Null(directive.ParentDirectiveId);
        Assert.Equal("Review the incoming production request and report the conclusion.", directive.Objective);
        Assert.Contains("Completion criteria", directive.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_root_directive_records_submission_audit_without_free_text_payload()
    {
        var sink = new CapturingDirectiveSubmissionSink();
        var audit = new RecordingJourneyAuditLog();
        await using var app = BuildApp(sink, audit);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            ValidRequest(from: "ceo", to: "delivery-lead"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            [JourneyAuditStage.SubmissionReceived, JourneyAuditStage.DirectiveCreated],
            audit.Records.Select(record => record.Stage));
        Assert.All(audit.Records, record =>
        {
            Assert.Equal(JourneyAuditOutcome.Accepted, record.Outcome);
            Assert.Equal(OrganizationId.From("acme-delivery"), record.OrganizationId);
            Assert.Equal(ThreadId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000111")), record.ThreadId);
            Assert.Equal(DirectiveId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000112")), record.DirectiveId);
            Assert.Equal(MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000110")), record.MessageId);
            Assert.Equal(PositionId.From("delivery-lead"), record.PositionId);
            Assert.DoesNotContain(
                "Review the incoming production request",
                string.Join(" ", record.Payload.Values),
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Submit_root_directive_records_deterministic_submission_audit_ids()
    {
        var sink = new CapturingDirectiveSubmissionSink();
        var audit = new RecordingJourneyAuditLog();
        await using var app = BuildApp(sink, audit);
        await app.StartAsync();
        using var client = app.GetTestClient();

        await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            ValidRequest(from: "ceo", to: "delivery-lead"));
        await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            ValidRequest(from: "ceo", to: "delivery-lead"));

        Assert.Equal(4, audit.Records.Count);
        Assert.Equal(audit.Records[0].AuditEventId, audit.Records[2].AuditEventId);
        Assert.Equal(audit.Records[1].AuditEventId, audit.Records[3].AuditEventId);
        Assert.NotEqual(audit.Records[0].AuditEventId, audit.Records[1].AuditEventId);
        Assert.Equal(
            JourneyAuditIdempotencyKey.From(
                JourneyAuditStage.SubmissionReceived,
                JourneyAuditOutcome.Accepted,
                audit.Records[0].OrganizationId,
                audit.Records[0].ThreadId,
                audit.Records[0].MessageId,
                audit.Records[0].DirectiveId,
                audit.Records[0].PositionId,
                audit.Records[0].MessageType).AuditEventId,
            audit.Records[0].AuditEventId);
    }

    [Fact]
    public async Task Submit_root_directive_validates_route_and_dispatches_to_position_shard()
    {
        var dispatcher = new RecordingPositionCommandDispatcher();
        await using var app = BuildShardedApp(dispatcher, SampleRelations());
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            ValidRequest(from: "ceo", to: "delivery-lead"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = Assert.Single(dispatcher.Envelopes);
        Assert.Equal(
            PositionEntityId.From(OrganizationId.From("acme-delivery"), PositionId.From("delivery-lead")),
            envelope.Position);
        var command = Assert.IsType<AcceptMessage>(envelope.Command);
        var directive = Assert.IsType<Directive>(command.Message);
        Assert.Equal("acme-delivery", directive.OrganizationId.Value);
        Assert.Equal("ceo", Assert.IsType<PositionEndpointRef>(directive.From).PositionId.Value);
        Assert.Equal("delivery-lead", Assert.IsType<PositionEndpointRef>(directive.To).PositionId.Value);
    }

    [Fact]
    public async Task Submit_root_directive_rejects_invalid_vertical_route_before_dispatch()
    {
        var dispatcher = new RecordingPositionCommandDispatcher();
        var audit = new RecordingJourneyAuditLog();
        await using var app = BuildShardedApp(dispatcher, SampleRelations(), audit);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            ValidRequest(from: "ceo", to: "triage-agent"));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Directive submission rejected", problem!.Title);
        Assert.Empty(dispatcher.Envelopes);
        var errors = Assert.IsType<JsonElement>(problem.Extensions["errors"]);
        var error = Assert.Single(errors.EnumerateArray());
        Assert.Equal("invalid-route", error.GetProperty("code").GetString());
        Assert.Equal("$", error.GetProperty("path").GetString());

        var rejection = Assert.Single(
            audit.Records,
            record => record.Stage == JourneyAuditStage.PositionAccepted);
        Assert.Equal(JourneyAuditOutcome.Rejected, rejection.Outcome);
        Assert.Equal("invalid-route", rejection.ReasonCode);
    }

    [Fact]
    public void AddHiveDirectiveSubmissionApi_registers_sharded_submission_sink_by_default()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(SampleRelations());
        builder.Services.AddSingleton<IPositionCommandDispatcher, RecordingPositionCommandDispatcher>();

        builder.Services.AddHiveDirectiveSubmissionApi();
        using var app = builder.Build();

        Assert.IsType<ShardedDirectiveSubmissionSink>(
            app.Services.GetRequiredService<IDirectiveSubmissionSink>());
    }

    [Fact]
    public async Task Submit_root_directive_rejects_parent_directive_id()
    {
        var sink = new CapturingDirectiveSubmissionSink();
        await using var app = BuildApp(sink);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            new
            {
                messageId = "aaaaaaaa-0000-0000-0000-000000000020",
                from = new { kind = "position", positionId = "ceo" },
                to = new { kind = "position", positionId = "delivery-lead" },
                threadId = "aaaaaaaa-0000-0000-0000-000000000021",
                priority = "normal",
                schemaVersion = 1,
                sentAt = "2026-07-07T10:15:30Z",
                directiveId = "aaaaaaaa-0000-0000-0000-000000000022",
                parentDirectiveId = "aaaaaaaa-0000-0000-0000-000000000023",
                objective = "Review the incoming production request.",
                context = "Completion criteria: decide next action.",
            });
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid directive submission", problem!.Title);
        Assert.Equal("parentDirectiveId", Assert.IsType<JsonElement>(problem.Extensions["path"]).GetString());
        Assert.Null(sink.Submitted);
    }

    [Fact]
    public async Task Submit_root_directive_rejects_non_position_endpoint_variants()
    {
        var sink = new CapturingDirectiveSubmissionSink();
        await using var app = BuildApp(sink);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            new
            {
                messageId = "aaaaaaaa-0000-0000-0000-000000000030",
                from = new { kind = "organization-owner" },
                to = new { kind = "position", positionId = "delivery-lead" },
                threadId = "aaaaaaaa-0000-0000-0000-000000000031",
                priority = "normal",
                schemaVersion = 1,
                sentAt = "2026-07-07T10:15:30Z",
                directiveId = "aaaaaaaa-0000-0000-0000-000000000032",
                objective = "Review the incoming production request.",
                context = "Completion criteria: decide next action.",
            });
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid directive submission", problem!.Title);
        Assert.Equal("from.kind", Assert.IsType<JsonElement>(problem.Extensions["path"]).GetString());
        Assert.Null(sink.Submitted);
    }

    private static WebApplication BuildApp(
        IDirectiveSubmissionSink sink,
        IJourneyAuditLog? auditLog = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(sink);
        if (auditLog is not null)
        {
            builder.Services.AddSingleton(auditLog);
        }

        var app = builder.Build();
        app.MapHiveDirectiveSubmissionApi();
        return app;
    }

    private static WebApplication BuildShardedApp(
        IPositionCommandDispatcher dispatcher,
        IOrganizationRelations relations,
        IJourneyAuditLog? auditLog = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(relations);
        builder.Services.AddSingleton(dispatcher);
        if (auditLog is not null)
        {
            builder.Services.AddSingleton(auditLog);
        }

        builder.Services.AddHiveDirectiveSubmissionApi();

        var app = builder.Build();
        app.MapHiveDirectiveSubmissionApi();
        return app;
    }

    private static object ValidRequest(string from, string to) =>
        new
        {
            messageId = "aaaaaaaa-0000-0000-0000-000000000110",
            from = new { kind = "position", positionId = from },
            to = new { kind = "position", positionId = to },
            threadId = "aaaaaaaa-0000-0000-0000-000000000111",
            priority = "high",
            schemaVersion = 1,
            sentAt = SentAt,
            directiveId = "aaaaaaaa-0000-0000-0000-000000000112",
            objective = "Review the incoming production request and report the conclusion.",
            context = "Completion criteria: severity, missing information, and next action.",
        };

    private static IOrganizationRelations SampleRelations() =>
        new MaterializedOrganizationRelations(
            OrganizationRelationsSnapshot
                .CreateBuilder(OrganizationId.From("acme-delivery"), new OrganizationOwnerEndpointRef())
                .AddPosition(PositionId.From("ceo"), UnitId.From("root"))
                .AddPosition(
                    PositionId.From("delivery-lead"),
                    UnitId.From("delivery"),
                    PositionId.From("ceo"))
                .AddPosition(
                    PositionId.From("triage-agent"),
                    UnitId.From("delivery"),
                    PositionId.From("delivery-lead"))
                .Build());

    private sealed class CapturingDirectiveSubmissionSink : IDirectiveSubmissionSink
    {
        public OrgMessage? Submitted { get; private set; }

        public ValueTask<DirectiveSubmissionResult> SubmitAsync(
            Directive directive,
            CancellationToken cancellationToken)
        {
            Submitted = directive;
            return ValueTask.FromResult(DirectiveSubmissionResult.Accepted(directive));
        }
    }

    private sealed class RecordingPositionCommandDispatcher : IPositionCommandDispatcher
    {
        private readonly List<PositionEnvelope> _envelopes = [];

        public IReadOnlyList<PositionEnvelope> Envelopes => _envelopes;

        public ValueTask DispatchAsync(
            PositionEnvelope envelope,
            CancellationToken cancellationToken)
        {
            _envelopes.Add(envelope);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingJourneyAuditLog : IJourneyAuditLog
    {
        private readonly List<JourneyAuditRecord> _records = [];

        public IReadOnlyList<JourneyAuditRecord> Records => _records;

        public void Append(JourneyAuditRecord record)
        {
            _records.Add(record);
        }

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) =>
            _records
                .Where(record => record.ThreadId == threadId &&
                    (directiveId is null || record.DirectiveId == directiveId))
                .ToArray();
    }
}
