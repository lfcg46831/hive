using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Infrastructure.Connectors;
using Microsoft.Extensions.Options;

namespace Hive.Connectors.GitHub.Tests;

public sealed class GitHubIssuesInboundProcessorTests
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Completion_conflict_replays_the_same_ids_and_converges_on_actor_deduplication()
    {
        var envelope = IssueEnvelope();
        var store = new ProcessingStore(envelope) { ConflictNextCompletion = true };
        var sink = new RecordingSubmissionSink();
        var processor = Processor(store, sink);

        var first = Assert.Single((await processor.ProcessPendingAsync()).Events);
        var second = Assert.Single((await processor.ProcessPendingAsync()).Events);

        Assert.Equal(GitHubIssuesInboundProcessingStatus.CompletionConflict, first.Status);
        Assert.Equal(GitHubIssuesInboundProcessingStatus.Submitted, second.Status);
        Assert.Equal(2, sink.Messages.Count);
        Assert.Equal(sink.Messages[0].Id, sink.Messages[1].Id);
        Assert.Equal(sink.Messages[0].Thread, sink.Messages[1].Thread);
        Assert.Equal(
            Assert.IsType<Directive>(sink.Messages[0]).DirectiveId,
            Assert.IsType<Directive>(sink.Messages[1]).DirectiveId);
        Assert.Equal(
            ConnectorMessageSubmissionDecision.Accepted,
            sink.Decisions[0]);
        Assert.Equal(
            ConnectorMessageSubmissionDecision.AlreadyAccepted,
            sink.Decisions[1]);
        Assert.Equal(
            GitHubIssuesInboundCompletionState.Submitted,
            store.Completions[envelope.ExternalEventId].State);
        Assert.Empty((await processor.ProcessPendingAsync()).Events);
    }

    [Fact]
    public async Task Invalid_payload_is_terminal_while_technical_submission_failure_remains_pending()
    {
        var invalid = new GitHubIssuesInboundEnvelope(
            "acme-github",
            "acme/payments",
            "issue:invalid",
            GitHubIssuesInboundEventKinds.Issue,
            "{\"title\":\"No number\"}",
            CapturedAt);
        var valid = IssueEnvelope();
        var store = new ProcessingStore(invalid, valid);
        var sink = new RecordingSubmissionSink { Fail = true };
        var processor = Processor(store, sink);

        var first = (await processor.ProcessPendingAsync()).Events;

        Assert.Contains(first, result =>
            result.ExternalEventId == invalid.ExternalEventId
            && result.Status == GitHubIssuesInboundProcessingStatus.Rejected
            && result.ReasonCode == GitHubIssuesInboundProcessingReasonCodes.PayloadInvalid);
        Assert.Contains(first, result =>
            result.ExternalEventId == valid.ExternalEventId
            && result.Status == GitHubIssuesInboundProcessingStatus.Failed
            && result.ReasonCode == GitHubIssuesInboundProcessingReasonCodes.ProcessingFailed);
        Assert.Equal(
            GitHubIssuesInboundCompletionState.Rejected,
            store.Completions[invalid.ExternalEventId].State);
        Assert.False(store.Completions.ContainsKey(valid.ExternalEventId));

        sink.Fail = false;
        var retry = Assert.Single((await processor.ProcessPendingAsync()).Events);

        Assert.Equal(valid.ExternalEventId, retry.ExternalEventId);
        Assert.Equal(GitHubIssuesInboundProcessingStatus.Submitted, retry.Status);
        Assert.Equal(
            GitHubIssuesInboundCompletionState.Submitted,
            store.Completions[valid.ExternalEventId].State);
    }

    [Fact]
    public async Task Root_leadership_target_is_rejected_without_submitting()
    {
        var envelope = IssueEnvelope();
        var store = new ProcessingStore(envelope);
        var sink = new RecordingSubmissionSink();
        var relations = Relations(targetIsRoot: true);
        var processor = new GitHubIssuesInboundProcessor(
            Catalog(target: "delivery-lead"),
            store,
            relations,
            new DirectiveRoutingValidator(relations),
            sink,
            new ManualTimeProvider(CapturedAt));

        var result = Assert.Single((await processor.ProcessPendingAsync()).Events);

        Assert.Equal(GitHubIssuesInboundProcessingStatus.Rejected, result.Status);
        Assert.Equal(
            GitHubIssuesInboundProcessingReasonCodes.TargetHasNoSuperior,
            result.ReasonCode);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task Subsequent_comment_reuses_the_persisted_issue_thread()
    {
        var envelope = new GitHubIssuesInboundEnvelope(
            "acme-github",
            "acme/payments",
            "comment:9001",
            GitHubIssuesInboundEventKinds.Comment,
            "{\"issue_number\":42,\"id\":9001,\"body\":\"Still failing.\"}",
            CapturedAt);
        var store = new ProcessingStore(envelope);
        var persisted = new GitHubIssueCorrelation(
            "acme-github",
            OrganizationId.From("acme"),
            "acme/payments",
            42,
            ThreadId.From(Guid.Parse("8a478688-5636-4fde-b67d-a889038b48b0")),
            DirectiveId.From(Guid.Parse("049ef8c7-e301-405f-8e80-4b1ccbbf9964")));
        store.Seed(persisted);
        var sink = new RecordingSubmissionSink();

        var result = Assert.Single((await Processor(store, sink).ProcessPendingAsync()).Events);

        Assert.Equal(GitHubIssuesInboundProcessingStatus.Submitted, result.Status);
        var directive = Assert.IsType<Directive>(Assert.Single(sink.Messages));
        Assert.Equal(persisted.ThreadId, directive.Thread);
        Assert.Equal(
            persisted,
            await store.FindCorrelationByDirectiveAsync(
                "acme-github",
                OrganizationId.From("acme"),
                directive.DirectiveId));
    }

    [Fact]
    public async Task Correlation_store_failure_leaves_the_event_pending_without_submission()
    {
        var envelope = IssueEnvelope();
        var store = new ProcessingStore(envelope) { FailCorrelationReads = true };
        var sink = new RecordingSubmissionSink();

        var result = Assert.Single((await Processor(store, sink).ProcessPendingAsync()).Events);

        Assert.Equal(GitHubIssuesInboundProcessingStatus.Failed, result.Status);
        Assert.Equal(
            GitHubIssuesInboundProcessingReasonCodes.ProcessingFailed,
            result.ReasonCode);
        Assert.Empty(sink.Messages);
        Assert.Empty(store.Completions);
    }

    private static GitHubIssuesInboundProcessor Processor(
        IGitHubIssuesInboundStore store,
        IConnectorMessageSubmissionSink sink)
    {
        var relations = Relations(targetIsRoot: false);
        return new GitHubIssuesInboundProcessor(
            Catalog("triage"),
            store,
            relations,
            new DirectiveRoutingValidator(relations),
            sink,
            new ManualTimeProvider(CapturedAt));
    }

    private static MaterializedOrganizationRelations Relations(bool targetIsRoot)
    {
        var root = PositionId.From("delivery-lead");
        var builder = OrganizationRelationsSnapshot
            .CreateBuilder(OrganizationId.From("acme"), new OrganizationOwnerEndpointRef())
            .AddPosition(root, UnitId.From("delivery"));
        if (!targetIsRoot)
        {
            builder.AddPosition(PositionId.From("triage"), UnitId.From("delivery"), root);
        }

        return new MaterializedOrganizationRelations(builder.Build());
    }

    private static GitHubIssuesConnectorConfigurationCatalog Catalog(string target) =>
        new(Options.Create(new GitHubIssuesConnectorOptions
        {
            Instances =
            [
                new GitHubIssuesConnectorInstanceOptions
                {
                    InstanceId = "acme-github",
                    OrganizationId = "acme",
                    Repositories = ["acme/payments"],
                    InboundDirectiveTarget = target,
                    OutboundOperations = [],
                    Polling = new GitHubIssuesPollingOptions
                    {
                        Interval = "PT1M",
                        PageSize = 100,
                    },
                },
            ],
            Credentials =
            [
                new GitHubIssuesConnectorCredentialOptions
                {
                    InstanceId = "acme-github",
                    Token = "test-token",
                },
            ],
        }));

    private static GitHubIssuesInboundEnvelope IssueEnvelope() =>
        new(
            "acme-github",
            "acme/payments",
            "issue:42",
            GitHubIssuesInboundEventKinds.Issue,
            "{\"number\":42,\"title\":\"Retry failed\",\"body\":\"Observed.\"}",
            CapturedAt);

    private sealed class ProcessingStore(params GitHubIssuesInboundEnvelope[] envelopes)
        : IGitHubIssuesInboundStore
    {
        public bool ConflictNextCompletion { get; set; }

        public bool FailCorrelationReads { get; set; }

        public Dictionary<string, GitHubIssuesInboundCompletion> Completions { get; } =
            new(StringComparer.Ordinal);

        private readonly Dictionary<
            (string InstanceId, string OrganizationId, string Repository, long IssueNumber),
            GitHubIssueCorrelation> _issues = [];
        private readonly Dictionary<
            (string InstanceId, string OrganizationId, Guid ThreadId),
            GitHubIssueCorrelation> _threads = [];
        private readonly Dictionary<
            (string InstanceId, string OrganizationId, Guid DirectiveId),
            GitHubIssueCorrelation> _directives = [];

        public void Seed(GitHubIssueCorrelation correlation) =>
            Record(correlation, directiveId: null);

        public ValueTask<GitHubIssuesPollingCheckpoint?> ReadCheckpointAsync(
            string instanceId,
            string repository,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GitHubIssuesInboundCommitResult> CommitBatchAsync(
            GitHubIssuesPollingCheckpoint? expectedCheckpoint,
            GitHubIssuesInboundBatch batch,
            DateTimeOffset capturedAtUtc,
            DateTimeOffset nextPollAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GitHubIssuesInboundEnvelope>> ReadPendingAsync(
            string instanceId,
            string repository,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitHubIssuesInboundEnvelope>>(envelopes
                .Where(envelope => !Completions.ContainsKey(envelope.ExternalEventId))
                .Take(limit)
                .ToArray());

        public Task<bool> TryCompleteAsync(
            GitHubIssuesInboundEnvelope envelope,
            GitHubIssuesInboundCompletion completion,
            CancellationToken cancellationToken = default)
        {
            if (ConflictNextCompletion)
            {
                ConflictNextCompletion = false;
                return Task.FromResult(false);
            }

            if (!Completions.TryAdd(envelope.ExternalEventId, completion))
            {
                return Task.FromResult(false);
            }

            if (completion.Submission is { } submission)
            {
                Record(submission.Issue, submission.DirectiveId);
            }

            return Task.FromResult(true);
        }

        public ValueTask<GitHubIssueCorrelation?> FindCorrelationByIssueAsync(
            string instanceId,
            OrganizationId organizationId,
            string repository,
            long issueNumber,
            CancellationToken cancellationToken = default)
        {
            if (FailCorrelationReads)
            {
                throw new TimeoutException("simulated correlation-store failure");
            }

            return ValueTask.FromResult(_issues.GetValueOrDefault((
                instanceId,
                organizationId.Value,
                repository.ToLowerInvariant(),
                issueNumber)));
        }

        public ValueTask<GitHubIssueCorrelation?> FindCorrelationByThreadAsync(
            string instanceId,
            OrganizationId organizationId,
            ThreadId threadId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_threads.GetValueOrDefault((
                instanceId,
                organizationId.Value,
                threadId.Value)));

        public ValueTask<GitHubIssueCorrelation?> FindCorrelationByDirectiveAsync(
            string instanceId,
            OrganizationId organizationId,
            DirectiveId directiveId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_directives.GetValueOrDefault((
                instanceId,
                organizationId.Value,
                directiveId.Value)));

        private void Record(
            GitHubIssueCorrelation correlation,
            DirectiveId? directiveId)
        {
            _issues[(
                correlation.InstanceId,
                correlation.OrganizationId.Value,
                correlation.Repository,
                correlation.IssueNumber)] = correlation;
            _threads[(
                correlation.InstanceId,
                correlation.OrganizationId.Value,
                correlation.ThreadId.Value)] = correlation;
            if (directiveId is not null)
            {
                _directives[(
                    correlation.InstanceId,
                    correlation.OrganizationId.Value,
                    directiveId.Value)] = correlation;
            }
        }
    }

    private sealed class RecordingSubmissionSink : IConnectorMessageSubmissionSink
    {
        public bool Fail { get; set; }

        public List<OrgMessage> Messages { get; } = [];

        public List<ConnectorMessageSubmissionDecision> Decisions { get; } = [];

        public ValueTask<ConnectorMessageSubmissionResult> SubmitAsync(
            OrgMessage message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            if (Fail)
            {
                throw new TimeoutException("simulated technical failure");
            }

            var result = Messages.Count == 1
                ? ConnectorMessageSubmissionResult.Accepted()
                : ConnectorMessageSubmissionResult.AlreadyAccepted();
            Decisions.Add(result.Decision);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
