using System.Collections.Immutable;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Infrastructure.Connectors;
using Microsoft.Extensions.Options;

namespace Hive.Connectors.GitHub.Tests;

public sealed class GitHubIssuesOutboundExecutorTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("bug-triage");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private const string OperationKey =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Tools_publish_closed_strict_schemas_and_canonicalize_labels()
    {
        var comment = GitHubIssuesOutboundToolDefinitions.Get(
            GitHubIssuesOutboundOperations.Comment);
        var state = GitHubIssuesOutboundToolDefinitions.Get(
            GitHubIssuesOutboundOperations.UpdateState);
        var labels = GitHubIssuesOutboundToolDefinitions.Get(
            GitHubIssuesOutboundOperations.UpdateLabels);

        Assert.Equal(["body"], Required(comment));
        Assert.Equal(["state"], Required(state));
        Assert.Equal(["labels"], Required(labels));
        Assert.All(
            new[] { comment, state, labels },
            definition => Assert.False((bool)definition.ParametersSchema["additionalProperties"]!));

        Assert.True(GitHubIssuesOutboundOperation.TryParse(
            new AiToolCall(
                "call-labels",
                GitHubIssuesOutboundOperations.UpdateLabels,
                new Dictionary<string, object?> { ["labels"] = new[] { "urgent", "bug" } }),
            out var operation,
            out _));
        Assert.Equal(["bug", "urgent"], operation!.Labels);
        Assert.Equal("{\"labels\":[\"bug\",\"urgent\"]}", operation.CanonicalPayload);

        Assert.False(GitHubIssuesOutboundOperation.TryParse(
            new AiToolCall(
                "call-duplicate",
                GitHubIssuesOutboundOperations.UpdateLabels,
                new Dictionary<string, object?> { ["labels"] = new[] { "bug", "bug" } }),
            out _,
            out _));
        Assert.False(GitHubIssuesOutboundOperation.TryParse(
            new AiToolCall(
                "call-extra",
                GitHubIssuesOutboundOperations.Comment,
                new Dictionary<string, object?>
                {
                    ["body"] = "Published",
                    ["repository"] = "other/repository",
                }),
            out _,
            out _));
    }

    [Fact]
    public async Task Retry_backoff_audit_and_success_replay_share_one_operation_key()
    {
        var issue = Correlation();
        var correlations = new CorrelationStore(issue);
        var store = new RecordingOutboundStore();
        var client = new SequenceClient(
            GitHubIssuesOutboundClientResult.Failed("github-rate-limited", retryable: true),
            GitHubIssuesOutboundClientResult.Failed("github-unavailable", retryable: true),
            GitHubIssuesOutboundClientResult.Success("receipt-42"));
        var backoff = new RecordingBackoff();
        var audit = new RecordingAuditLog();
        var executor = new GitHubIssuesOutboundExecutor(
            Catalog(),
            correlations,
            store,
            client,
            backoff,
            audit,
            new FixedTimeProvider(At));
        var invocation = Invocation("Published after validation.");

        var first = await executor.ExecuteAsync(invocation);
        var replay = await executor.ExecuteAsync(invocation);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal("succeeded", first.Output["status"]);
        Assert.Equal("already-succeeded", replay.Output["status"]);
        Assert.Equal(3, client.Requests.Count);
        Assert.All(client.Requests, request => Assert.Equal(OperationKey, request.OperationKey));
        Assert.Equal(
            [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)],
            backoff.Delays);
        Assert.Equal(3, store.State.AttemptCount);
        Assert.Equal(GitHubIssuesOutboundOperationState.Succeeded, store.State.State);
        Assert.Equal("receipt-42", store.State.Receipt);
        Assert.Equal(2, store.AcquireCount);
        Assert.Equal(5, audit.Records.Count);
        Assert.All(audit.Records, record =>
        {
            Assert.Equal(JourneyAuditStage.ConnectorOutbound, record.Stage);
            Assert.Equal(Organization, record.OrganizationId);
            Assert.Equal(Thread, record.ThreadId);
            Assert.DoesNotContain("Published after validation.",
                string.Join("|", record.Payload.Values), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Invalid_or_unrelated_action_fails_before_store_and_client()
    {
        var store = new RecordingOutboundStore();
        var client = new SequenceClient(GitHubIssuesOutboundClientResult.Success("unused"));
        var executor = new GitHubIssuesOutboundExecutor(
            Catalog(),
            new CorrelationStore(correlation: null),
            store,
            client,
            new RecordingBackoff(),
            new RecordingAuditLog(),
            new FixedTimeProvider(At));

        var invalid = await executor.ExecuteAsync(Invocation(" "));
        var missing = await executor.ExecuteAsync(Invocation("Valid"));

        Assert.Equal("github-outbound-arguments-invalid", invalid.ErrorCode);
        Assert.Equal("github-outbound-correlation-not-found", missing.ErrorCode);
        Assert.Equal(0, store.AcquireCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Disabled_outbound_operation_is_scope_denied_and_audited_before_store_or_client()
    {
        var store = new RecordingOutboundStore();
        var client = new SequenceClient(GitHubIssuesOutboundClientResult.Success("unused"));
        var audit = new RecordingAuditLog();
        var executor = new GitHubIssuesOutboundExecutor(
            Catalog(outboundOperations: [GitHubIssuesOutboundOperations.UpdateState]),
            new CorrelationStore(Correlation()),
            store,
            client,
            new RecordingBackoff(),
            audit,
            new FixedTimeProvider(At));

        var result = await executor.ExecuteAsync(Invocation("must stay private"));

        Assert.False(result.IsSuccess);
        Assert.False(result.Retryable);
        Assert.Equal(GitHubIssuesScopePolicy.ScopeDeniedCode, result.ErrorCode);
        Assert.Equal(0, store.AcquireCount);
        Assert.Empty(client.Requests);
        var record = Assert.Single(audit.Records);
        Assert.Equal(JourneyAuditStage.ConnectorOutbound, record.Stage);
        Assert.Equal(JourneyAuditOutcome.Rejected, record.Outcome);
        Assert.Equal(GitHubIssuesScopePolicy.ScopeDeniedCode, record.ReasonCode);
        Assert.Equal("outbound", record.Payload["direction"]);
        Assert.Equal(GitHubIssuesScopeDimensions.Operation, record.Payload["deniedDimension"]);
        Assert.DoesNotContain("must stay private", string.Join('|', record.Payload.Values));
    }

    [Fact]
    public async Task Out_of_scope_correlated_repository_is_denied_before_store_or_client()
    {
        var store = new RecordingOutboundStore();
        var client = new SequenceClient(GitHubIssuesOutboundClientResult.Success("unused"));
        var audit = new RecordingAuditLog();
        var executor = new GitHubIssuesOutboundExecutor(
            Catalog(),
            new CorrelationStore(Correlation("other/private")),
            store,
            client,
            new RecordingBackoff(),
            audit,
            new FixedTimeProvider(At));

        var result = await executor.ExecuteAsync(Invocation("must stay private"));

        Assert.Equal(GitHubIssuesScopePolicy.ScopeDeniedCode, result.ErrorCode);
        Assert.Equal(0, store.AcquireCount);
        Assert.Empty(client.Requests);
        var record = Assert.Single(audit.Records);
        Assert.Equal(JourneyAuditOutcome.Rejected, record.Outcome);
        Assert.Equal(GitHubIssuesScopeDimensions.Repository, record.Payload["deniedDimension"]);
        Assert.Equal("other/private", record.Payload["repository"]);
    }

    private static string[] Required(AiToolDefinition definition) =>
        Assert.IsType<string[]>(definition.ParametersSchema["required"]);

    private static ConnectorToolInvocation Invocation(string body) =>
        new(
            OperationKey,
            Organization,
            Position,
            Thread,
            Message,
            Directive,
            parentDirectiveId: null,
            new AiToolCall(
                "call-42",
                GitHubIssuesOutboundOperations.Comment,
                new Dictionary<string, object?> { ["body"] = body }));

    private static GitHubIssueCorrelation Correlation(
        string repository = "acme/payments") =>
        new(
            "acme-github",
            Organization,
            repository,
            42,
            Thread,
            Directive);

    private static GitHubIssuesConnectorConfigurationCatalog Catalog(
        IReadOnlyList<string>? repositories = null,
        IReadOnlyList<string>? outboundOperations = null) =>
        new(Options.Create(new GitHubIssuesConnectorOptions
        {
            Instances =
            [
                new GitHubIssuesConnectorInstanceOptions
                {
                    InstanceId = "acme-github",
                    OrganizationId = Organization.Value,
                    Repositories = (repositories ?? ["acme/payments"]).ToArray(),
                    InboundDirectiveTarget = Position.Value,
                    OutboundOperations = (outboundOperations ??
                    [
                        GitHubIssuesOutboundOperations.Comment,
                        GitHubIssuesOutboundOperations.UpdateState,
                        GitHubIssuesOutboundOperations.UpdateLabels,
                    ]).ToArray(),
                    Polling = new GitHubIssuesPollingOptions
                    {
                        Interval = "PT30S",
                        PageSize = 100,
                    },
                },
            ],
            Credentials =
            [
                new GitHubIssuesConnectorCredentialOptions
                {
                    InstanceId = "acme-github",
                    Token = "test-only",
                },
            ],
        }));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class RecordingBackoff : IGitHubIssuesOutboundBackoff
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class SequenceClient(params GitHubIssuesOutboundClientResult[] results)
        : IGitHubIssuesOutboundClient
    {
        private readonly Queue<GitHubIssuesOutboundClientResult> _results = new(results);

        public List<GitHubIssuesOutboundRequest> Requests { get; } = [];

        public Task<GitHubIssuesOutboundClientResult> ExecuteAsync(
            GitHubIssuesOutboundRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class CorrelationStore(GitHubIssueCorrelation? correlation)
        : IGitHubIssuesInboundStore
    {
        public ValueTask<GitHubIssueCorrelation?> FindCorrelationByDirectiveAsync(
            string instanceId,
            OrganizationId organizationId,
            DirectiveId directiveId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(correlation);

        public ValueTask<GitHubIssueCorrelation?> FindCorrelationByThreadAsync(
            string instanceId,
            OrganizationId organizationId,
            ThreadId threadId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(correlation);

        public ValueTask<GitHubIssueCorrelation?> FindCorrelationByIssueAsync(
            string instanceId,
            OrganizationId organizationId,
            string repository,
            long issueNumber,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(correlation);

        public ValueTask<GitHubIssuesPollingCheckpoint?> ReadCheckpointAsync(
            string instanceId,
            string repository,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitHubIssuesInboundCommitResult> CommitBatchAsync(
            GitHubIssuesPollingCheckpoint? expectedCheckpoint,
            GitHubIssuesInboundBatch batch,
            DateTimeOffset capturedAtUtc,
            DateTimeOffset nextPollAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<GitHubIssuesInboundEnvelope>> ReadPendingAsync(
            string instanceId,
            string repository,
            int limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryCompleteAsync(
            GitHubIssuesInboundEnvelope envelope,
            GitHubIssuesInboundCompletion completion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingOutboundStore : IGitHubIssuesOutboundStore
    {
        private GitHubIssuesOutboundOperationDescriptor? _descriptor;

        public int AcquireCount { get; private set; }

        public GitHubIssuesOutboundOperationSnapshot State { get; private set; } =
            new(GitHubIssuesOutboundOperationState.Pending, 0, null, null);

        public Task<IGitHubIssuesOutboundOperationLease> AcquireAsync(
            GitHubIssuesOutboundOperationDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            if (_descriptor is not null
                && (_descriptor.OperationKey != descriptor.OperationKey
                    || _descriptor.PayloadHash != descriptor.PayloadHash))
            {
                throw new GitHubIssuesOutboundOperationConflictException();
            }

            _descriptor ??= descriptor;
            return Task.FromResult<IGitHubIssuesOutboundOperationLease>(new Lease(this));
        }

        private sealed class Lease(RecordingOutboundStore owner)
            : IGitHubIssuesOutboundOperationLease
        {
            public GitHubIssuesOutboundOperationSnapshot Snapshot => owner.State;

            public Task RecordAttemptAsync(
                string code,
                DateTimeOffset attemptedAtUtc,
                CancellationToken cancellationToken = default)
            {
                owner.State = owner.State with
                {
                    AttemptCount = owner.State.AttemptCount + 1,
                    LastCode = code,
                };
                return Task.CompletedTask;
            }

            public Task CompleteSuccessAsync(
                string receipt,
                DateTimeOffset completedAtUtc,
                CancellationToken cancellationToken = default)
            {
                owner.State = new(
                    GitHubIssuesOutboundOperationState.Succeeded,
                    owner.State.AttemptCount,
                    null,
                    receipt);
                return Task.CompletedTask;
            }

            public Task CompleteRejectedAsync(
                string errorCode,
                DateTimeOffset completedAtUtc,
                CancellationToken cancellationToken = default)
            {
                owner.State = new(
                    GitHubIssuesOutboundOperationState.Rejected,
                    owner.State.AttemptCount,
                    errorCode,
                    null);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAuditLog : IJourneyAuditLog
    {
        public List<JourneyAuditRecord> Records { get; } = [];

        public void Append(JourneyAuditRecord record) => Records.Add(record);

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) =>
            Records.Where(record => record.ThreadId == threadId
                && (directiveId is null || record.DirectiveId == directiveId)).ToArray();
    }
}
