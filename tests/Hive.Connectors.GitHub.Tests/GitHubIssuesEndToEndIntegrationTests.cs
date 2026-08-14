using System.Net;
using System.Text;
using System.Text.Json;
using Hive.Connectors.GitHub.PostgreSql;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Infrastructure.Connectors;
using Hive.Infrastructure.Governance;
using Hive.Infrastructure.Organization.Configuration;
using Microsoft.Extensions.Options;

namespace Hive.Connectors.GitHub.Tests;

[Collection(GitHubPostgreSqlCollection.Name)]
public sealed class GitHubIssuesEndToEndIntegrationTests(
    GitHubPostgreSqlFixture fixture)
{
    private const string InstanceId = "acme-github";
    private const string Repository = "acme/payments";
    private static readonly DateTimeOffset At =
        new(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Triage = PositionId.From("bug-triage");
    private static readonly PositionId DeliveryLead = PositionId.From("delivery-lead");
    private static readonly UnitId Delivery = UnitId.From("delivery");
    private static readonly MessageId SourceMessage =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task Polling_checkpoint_restart_and_replay_submit_one_directive_per_external_event()
    {
        await ResetAndMigrateAsync();
        var catalog = Catalog();
        var clock = new MutableTimeProvider(At);
        var client = new ReplayInboundClient(
            Batch(
                "cursor-1",
                IssueEvent()),
            Batch(
                "cursor-2",
                IssueEvent(),
                CommentEvent()),
            Batch(
                "cursor-3",
                IssueEvent(),
                CommentEvent()));
        var sink = new DeduplicatingSubmissionSink();

        await using (var firstStore =
                     new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString))
        {
            var firstPoll = Assert.Single((await Poller(
                catalog,
                client,
                firstStore,
                clock).PollDueRepositoriesAsync()).Repositories);
            var firstProcessing = Assert.Single((await Processor(
                catalog,
                firstStore,
                sink,
                clock).ProcessPendingAsync()).Events);

            Assert.Equal(GitHubIssuesRepositoryPollStatus.Committed, firstPoll.Status);
            Assert.Equal(1, firstPoll.InsertedCount);
            Assert.Equal(GitHubIssuesInboundProcessingStatus.Submitted, firstProcessing.Status);
        }

        clock.AdvanceTo(At.AddSeconds(1));
        await using (var restartedStore =
                     new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString))
        {
            var restartedPoll = Assert.Single((await Poller(
                catalog,
                client,
                restartedStore,
                clock).PollDueRepositoriesAsync()).Repositories);
            var restartedProcessing = Assert.Single((await Processor(
                catalog,
                restartedStore,
                sink,
                clock).ProcessPendingAsync()).Events);

            Assert.Equal("cursor-1", client.Cursors[1]);
            Assert.Equal(1, restartedPoll.InsertedCount);
            Assert.Equal("comment:9001", restartedProcessing.ExternalEventId);
            Assert.Equal(
                GitHubIssuesInboundProcessingStatus.Submitted,
                restartedProcessing.Status);
        }

        clock.AdvanceTo(At.AddSeconds(2));
        await using (var replayStore =
                     new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString))
        {
            var replayPoll = Assert.Single((await Poller(
                catalog,
                client,
                replayStore,
                clock).PollDueRepositoriesAsync()).Repositories);
            var replayProcessing = await Processor(
                catalog,
                replayStore,
                sink,
                clock).ProcessPendingAsync();
            var checkpoint = await replayStore.ReadCheckpointAsync(InstanceId, Repository);

            Assert.Equal("cursor-2", client.Cursors[2]);
            Assert.Equal(0, replayPoll.InsertedCount);
            Assert.Empty(replayProcessing.Events);
            Assert.Equal("cursor-3", checkpoint!.Cursor);
        }

        var directives = sink.Messages.Cast<Directive>().ToArray();
        Assert.Equal(2, directives.Length);
        Assert.Equal(directives[0].Thread, directives[1].Thread);
        Assert.NotEqual(directives[0].DirectiveId, directives[1].DirectiveId);
        Assert.Equal([null, "cursor-1", "cursor-2"], client.Cursors);
    }

    [Fact]
    public async Task Approved_outbound_comment_is_published_once_across_retry_and_store_restart()
    {
        await ResetAndMigrateAsync();
        var correlation = Correlation();
        await SeedCorrelationAsync(correlation);
        var gate = ResolveExampleGate(GitHubIssuesOutboundOperations.Comment);
        Assert.Equal(ActionGateOutcome.Allowed, gate.Outcome);

        var catalog = Catalog();
        var handler = new UnknownCommitCommentHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: true);
        var client = new GitHubIssuesRestClient(httpClient, catalog, new FixedTimeProvider(At));
        var backoff = new RecordingBackoff();
        var invocation = Invocation(
            correlation,
            new string('a', 64),
            GitHubIssuesOutboundOperations.Comment,
            "body",
            "Published after approval.");

        await using (var inboundStore =
                     new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString))
        await using (var outboundStore =
                     new PostgreSqlGitHubIssuesOutboundStore(fixture.ConnectionString))
        {
            var first = await Executor(
                catalog,
                inboundStore,
                outboundStore,
                client,
                backoff).ExecuteAsync(invocation);

            Assert.True(first.IsSuccess);
            Assert.Equal("succeeded", first.Output["status"]);
            Assert.Equal([TimeSpan.FromMilliseconds(100)], backoff.Delays);
            Assert.Equal(1, handler.PublishedEffects);
            Assert.Equal(
                [HttpMethod.Get, HttpMethod.Post, HttpMethod.Get],
                handler.Methods);
        }

        await using (var restartedInboundStore =
                     new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString))
        await using (var restartedOutboundStore =
                     new PostgreSqlGitHubIssuesOutboundStore(fixture.ConnectionString))
        {
            var replay = await Executor(
                catalog,
                restartedInboundStore,
                restartedOutboundStore,
                client,
                new RecordingBackoff()).ExecuteAsync(invocation);

            Assert.True(replay.IsSuccess);
            Assert.Equal("already-succeeded", replay.Output["status"]);
            Assert.Equal(1, handler.PublishedEffects);
            Assert.Equal(3, handler.Methods.Count);
        }
    }

    [Fact]
    public async Task Out_of_scope_outbound_is_rejected_before_operation_state_or_http_effect()
    {
        await ResetAndMigrateAsync();
        var correlation = Correlation("other/private");
        await SeedCorrelationAsync(correlation);
        var catalog = Catalog();
        var handler = new SuccessfulStateHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: true);
        var client = new GitHubIssuesRestClient(httpClient, catalog, new FixedTimeProvider(At));

        await using var inboundStore =
            new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString);
        await using var outboundStore =
            new PostgreSqlGitHubIssuesOutboundStore(fixture.ConnectionString);
        var result = await Executor(
            catalog,
            inboundStore,
            outboundStore,
            client,
            new RecordingBackoff()).ExecuteAsync(Invocation(
                correlation,
                new string('b', 64),
                GitHubIssuesOutboundOperations.Comment,
                "body",
                "Must not leave HIVE."));

        Assert.False(result.IsSuccess);
        Assert.False(result.Retryable);
        Assert.Equal(GitHubIssuesScopePolicy.ScopeDeniedCode, result.ErrorCode);
        Assert.Empty(handler.Methods);
        Assert.Equal(0, await OutboundOperationCountAsync());
    }

    [Fact]
    public async Task Human_approval_gate_retains_state_change_until_a_favorable_decision()
    {
        await ResetAndMigrateAsync();
        var correlation = Correlation();
        await SeedCorrelationAsync(correlation);
        var gate = ResolveExampleGate(
            GitHubIssuesOutboundOperations.UpdateState,
            state: "closed");
        var requirement = Assert.Single(gate.RequiredApprovals);
        Assert.Equal(ActionGateOutcome.HumanApprovalRequired, gate.Outcome);
        Assert.Equal(DeliveryLead.Value, requirement.Approver);

        var catalog = Catalog();
        var handler = new SuccessfulStateHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: true);
        var client = new GitHubIssuesRestClient(httpClient, catalog, new FixedTimeProvider(At));
        await using var inboundStore =
            new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString);
        await using var outboundStore =
            new PostgreSqlGitHubIssuesOutboundStore(fixture.ConnectionString);
        var executor = Executor(
            catalog,
            inboundStore,
            outboundStore,
            client,
            new RecordingBackoff());
        var invocation = Invocation(
            correlation,
            new string('c', 64),
            GitHubIssuesOutboundOperations.UpdateState,
            "state",
            "closed");

        // The connector executor is a post-gate seam: retained work must not cross it.
        Assert.Empty(handler.Methods);
        Assert.Equal(0, await OutboundOperationCountAsync());

        var request = ApprovalRequest(correlation.ThreadId);
        var decision = ApprovalDecision(request, approved: true);
        Assert.True(decision.Approved);

        var result = await executor.ExecuteAsync(invocation);

        Assert.True(result.IsSuccess);
        Assert.Equal([HttpMethod.Patch], handler.Methods);
        Assert.Equal(1, await OutboundOperationCountAsync());
    }

    private async Task ResetAndMigrateAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using (var reset = dataSource.CreateCommand(
                         "DROP SCHEMA IF EXISTS github_connector CASCADE;"))
        {
            await reset.ExecuteNonQueryAsync();
        }

        await new PostgreSqlGitHubIssuesInboundMigrator(dataSource).MigrateAsync();
    }

    private async Task SeedCorrelationAsync(GitHubIssueCorrelation correlation)
    {
        await using var store =
            new PostgreSqlGitHubIssuesInboundStore(fixture.ConnectionString);
        var batch = new GitHubIssuesInboundBatch(
            correlation.InstanceId,
            correlation.Repository,
            "seed-cursor",
            [IssueEvent()]);
        await store.CommitBatchAsync(
            expectedCheckpoint: null,
            batch,
            At,
            At.AddSeconds(1));
        var envelope = Assert.Single(await store.ReadPendingAsync(
            correlation.InstanceId,
            correlation.Repository,
            10));
        Assert.True(await store.TryCompleteAsync(
            envelope,
            new GitHubIssuesInboundCompletion(
                GitHubIssuesInboundCompletionState.Submitted,
                At.AddMilliseconds(1),
                submission: new GitHubIssueSubmissionCorrelation(
                    correlation,
                    correlation.RootDirectiveId))));
    }

    private async Task<long> OutboundOperationCountAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM github_connector.outbound_operations;");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static GitHubIssuesInboundPoller Poller(
        GitHubIssuesConnectorConfigurationCatalog catalog,
        IGitHubIssuesInboundClient client,
        IGitHubIssuesInboundStore store,
        TimeProvider timeProvider) =>
        new(catalog, client, store, NoopJourneyAuditLog.Instance, timeProvider);

    private static GitHubIssuesInboundProcessor Processor(
        GitHubIssuesConnectorConfigurationCatalog catalog,
        IGitHubIssuesInboundStore store,
        IConnectorMessageSubmissionSink sink,
        TimeProvider timeProvider)
    {
        var relations = OrganizationRelationsSnapshot
            .CreateBuilder(Organization, new OrganizationOwnerEndpointRef())
            .AddPosition(DeliveryLead, Delivery)
            .AddPosition(Triage, Delivery, DeliveryLead)
            .Build();
        var materialized = new MaterializedOrganizationRelations(relations);
        return new GitHubIssuesInboundProcessor(
            catalog,
            store,
            materialized,
            new DirectiveRoutingValidator(materialized),
            sink,
            timeProvider);
    }

    private static GitHubIssuesOutboundExecutor Executor(
        GitHubIssuesConnectorConfigurationCatalog catalog,
        IGitHubIssuesInboundStore correlations,
        IGitHubIssuesOutboundStore store,
        IGitHubIssuesOutboundClient client,
        IGitHubIssuesOutboundBackoff backoff) =>
        new(
            catalog,
            correlations,
            store,
            client,
            backoff,
            NoopJourneyAuditLog.Instance,
            new FixedTimeProvider(At));

    private static GitHubIssuesConnectorConfigurationCatalog Catalog() =>
        new(Options.Create(new GitHubIssuesConnectorOptions
        {
            Instances =
            [
                new GitHubIssuesConnectorInstanceOptions
                {
                    InstanceId = InstanceId,
                    OrganizationId = Organization.Value,
                    Repositories = [Repository],
                    InboundDirectiveTarget = Triage.Value,
                    OutboundOperations =
                    [
                        GitHubIssuesOutboundOperations.Comment,
                        GitHubIssuesOutboundOperations.UpdateState,
                    ],
                    Polling = new GitHubIssuesPollingOptions
                    {
                        Interval = "PT1S",
                        PageSize = 100,
                    },
                },
            ],
            Credentials =
            [
                new GitHubIssuesConnectorCredentialOptions
                {
                    InstanceId = InstanceId,
                    Token = "integration-token",
                },
            ],
        }));

    private static GitHubIssuesInboundBatch Batch(
        string cursor,
        params GitHubIssuesInboundEvent[] events) =>
        new(InstanceId, Repository, cursor, events);

    private static GitHubIssuesInboundEvent IssueEvent() =>
        new(
            "issue:42",
            GitHubIssuesInboundEventKinds.Issue,
            "{\"number\":42,\"title\":\"Retry failed\",\"body\":\"Observed.\"}");

    private static GitHubIssuesInboundEvent CommentEvent() =>
        new(
            "comment:9001",
            GitHubIssuesInboundEventKinds.Comment,
            "{\"issue_number\":42,\"id\":9001,\"body\":\"Still failing.\"}");

    private static GitHubIssueCorrelation Correlation(string repository = Repository) =>
        new(
            InstanceId,
            Organization,
            repository,
            42,
            ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")));

    private static ConnectorToolInvocation Invocation(
        GitHubIssueCorrelation correlation,
        string operationKey,
        string operation,
        string argument,
        object value) =>
        new(
            operationKey,
            Organization,
            Triage,
            correlation.ThreadId,
            SourceMessage,
            correlation.RootDirectiveId,
            parentDirectiveId: null,
            new AiToolCall(
                "call-integration",
                operation,
                new Dictionary<string, object?> { [argument] = value }));

    private static ActionGateResolution ResolveExampleGate(
        string operation,
        string? state = null)
    {
        var directory = Path.Combine(
            RepositoryRoot,
            "config",
            "organizations",
            "acme-delivery");
        var organizationResult = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(directory, "organization.yaml"));
        var catalogResult = new ActionDomainCatalogParser().ParseFile(
            Path.Combine(directory, "action-domains.yaml"));
        Assert.True(organizationResult.IsSuccess, string.Join(
            Environment.NewLine,
            organizationResult.Errors));
        Assert.True(catalogResult.IsSuccess, string.Join(
            Environment.NewLine,
            catalogResult.Errors));

        var triage = organizationResult.Configuration!.Positions.Single(
            position => position.Id == Triage);
        var authority = triage.Occupant.Authority!;
        var source = new GitHubIssuesActionDomainContractSource();
        var contract = source.ActionContracts.Single(item => item.SelectorValue == operation);
        var extractor = source.ActionExtractors.Single(item => item.SelectorValue == operation);
        IReadOnlyDictionary<string, ActionAttributeValue>? directAttributes = state is null
            ? null
            : new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
            {
                [GitHubIssuesActionAttributeNames.State] =
                    ActionAttributeValue.FromString(state),
            };
        var extraction = ActionAttributeExtractorRunner.Extract(
            contract,
            extractor,
            new ActionAttributeExtractionRequest(
                ActionDomainActionKind.Tool,
                operation,
                directAttributes));
        Assert.True(extraction.IsSuccess, extraction.Failure?.Code);

        return ActionGateResolver.Resolve(
            catalogResult.Catalog!,
            new ActionDomainAuthorityBinding(
                "positions[bug-triage].authority",
                authority.CanDecide,
                authority.Overrides.Select(item => new ActionDomainAuthorityOverride(
                    item.Key,
                    item.Gate,
                    item.Approver)).ToArray()),
            extraction.Facts!,
            ActingUnderDeclaration.Declared(
                AuthorityKey.From("delivery.bug-triage")));
    }

    private static ApprovalRequest ApprovalRequest(ThreadId threadId) =>
        new(
            MessageId.From(Guid.Parse("44444444-4444-4444-4444-444444444444")),
            Organization,
            new PositionEndpointRef(Triage),
            new PositionEndpointRef(DeliveryLead),
            threadId,
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: At.AddHours(1),
            action: GitHubIssuesOutboundOperations.UpdateState,
            justification: "Close the correlated GitHub issue.",
            ApprovalPolicyRef.From("action-domain-delivery-lead"));

    private static ApprovalDecision ApprovalDecision(
        ApprovalRequest request,
        bool approved) =>
        new(
            MessageId.From(Guid.Parse("55555555-5555-5555-5555-555555555555")),
            Organization,
            new PositionEndpointRef(DeliveryLead),
            new PositionEndpointRef(Triage),
            request.Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At.AddMinutes(1),
            deadline: null,
            request.Id,
            approved,
            reason: approved ? "Approved for delivery." : "Rejected.");

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class ReplayInboundClient(params GitHubIssuesInboundBatch[] batches)
        : IGitHubIssuesInboundClient
    {
        private readonly Queue<GitHubIssuesInboundBatch> _batches = new(batches);

        public List<string?> Cursors { get; } = [];

        public Task<GitHubIssuesInboundBatch> FetchBatchAsync(
            GitHubIssuesConnectorInstanceConfiguration instance,
            string repository,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Cursors.Add(cursor);
            return Task.FromResult(_batches.Dequeue());
        }
    }

    private sealed class DeduplicatingSubmissionSink : IConnectorMessageSubmissionSink
    {
        private readonly HashSet<MessageId> _accepted = [];

        public List<OrgMessage> Messages { get; } = [];

        public ValueTask<ConnectorMessageSubmissionResult> SubmitAsync(
            OrgMessage message,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_accepted.Add(message.Id))
            {
                return ValueTask.FromResult(
                    ConnectorMessageSubmissionResult.AlreadyAccepted());
            }

            Messages.Add(message);
            return ValueTask.FromResult(ConnectorMessageSubmissionResult.Accepted());
        }
    }

    private sealed class RecordingBackoff : IGitHubIssuesOutboundBackoff
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class UnknownCommitCommentHandler : HttpMessageHandler
    {
        private string? _remoteBody;

        public List<HttpMethod> Methods { get; } = [];

        public int PublishedEffects { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            if (request.Method == HttpMethod.Get)
            {
                return _remoteBody is null
                    ? JsonResponse(HttpStatusCode.OK, "[]")
                    : JsonResponse(
                        HttpStatusCode.OK,
                        JsonSerializer.Serialize(new[]
                        {
                            new { id = 55L, body = _remoteBody },
                        }));
            }

            using var document = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            _remoteBody = document.RootElement.GetProperty("body").GetString();
            PublishedEffects++;
            throw new HttpRequestException("Simulated lost acknowledgement after remote commit.");
        }
    }

    private sealed class SuccessfulStateHandler : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Methods.Add(request.Method);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;

        public void AdvanceTo(DateTimeOffset value) => _value = value;
    }
}
