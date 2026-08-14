using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Infrastructure.Connectors;

namespace Hive.Connectors.GitHub;

internal static class GitHubIssuesInboundProcessingReasonCodes
{
    public const string PayloadInvalid = "github-issues-payload-invalid";
    public const string TargetRelationInvalid = "github-issues-target-relation-invalid";
    public const string TargetHasNoSuperior = "github-issues-target-has-no-superior";
    public const string MappingFailed = "github-issues-mapping-failed";
    public const string RoutingRejected = "github-issues-routing-rejected";
    public const string ProcessingFailed = "github-issues-processing-failed";
    public const string CompletionConflict = "github-issues-completion-conflict";
}

internal enum GitHubIssuesInboundProcessingStatus
{
    Submitted = 1,
    Rejected = 2,
    Failed = 3,
    CompletionConflict = 4,
}

internal sealed record GitHubIssuesInboundProcessingResult(
    string InstanceId,
    string Repository,
    string ExternalEventId,
    GitHubIssuesInboundProcessingStatus Status,
    string? ReasonCode = null);

internal sealed record GitHubIssuesInboundProcessingCycleResult(
    IReadOnlyList<GitHubIssuesInboundProcessingResult> Events);

internal sealed class GitHubIssuesInboundProcessor(
    GitHubIssuesConnectorConfigurationCatalog catalog,
    IGitHubIssuesInboundStore store,
    IOrganizationRelations relations,
    DirectiveRoutingValidator routingValidator,
    IConnectorMessageSubmissionSink submissionSink,
    TimeProvider timeProvider) : IGitHubIssuesInboundProcessor
{
    private const int ProcessingPageSize = 100;

    public async Task<GitHubIssuesInboundProcessingCycleResult> ProcessPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<GitHubIssuesInboundProcessingResult>();
        foreach (var instance in catalog.Instances.OrderBy(
                     item => item.InstanceId,
                     StringComparer.Ordinal))
        {
            foreach (var repository in instance.Repositories.OrderBy(
                         value => value,
                         StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<GitHubIssuesInboundEnvelope> pending;
                try
                {
                    pending = await store
                        .ReadPendingAsync(
                            instance.InstanceId,
                            repository,
                            ProcessingPageSize,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    results.Add(new GitHubIssuesInboundProcessingResult(
                        instance.InstanceId,
                        repository,
                        "repository-read",
                        GitHubIssuesInboundProcessingStatus.Failed,
                        GitHubIssuesInboundProcessingReasonCodes.ProcessingFailed));
                    continue;
                }

                foreach (var envelope in pending)
                {
                    results.Add(await ProcessOneAsync(
                            instance,
                            envelope,
                            cancellationToken)
                        .ConfigureAwait(false));
                }
            }
        }

        return new GitHubIssuesInboundProcessingCycleResult(results);
    }

    private async Task<GitHubIssuesInboundProcessingResult> ProcessOneAsync(
        GitHubIssuesConnectorInstanceConfiguration instance,
        GitHubIssuesInboundEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var parsed = GitHubIssuesInboundPayloadParser.Parse(envelope);
        if (!parsed.IsSuccess)
        {
            return await RejectAsync(
                    envelope,
                    GitHubIssuesInboundProcessingReasonCodes.PayloadInvalid,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        GitHubIssueCorrelation correlation;
        try
        {
            correlation = await store
                .FindCorrelationByIssueAsync(
                    instance.InstanceId,
                    instance.OrganizationId,
                    envelope.Repository,
                    parsed.IssueNumber!.Value,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? GitHubIssuesInboundCorrelationFactory.Create(
                    instance,
                    envelope.Repository,
                    parsed.IssueNumber.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failed(envelope);
        }

        Hive.Domain.Identity.PositionId? source;
        try
        {
            source = await relations
                .GetDirectSuperiorAsync(
                    instance.OrganizationId,
                    instance.InboundDirectiveTarget,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrganizationRelationNotFoundException)
        {
            return await RejectAsync(
                    envelope,
                    GitHubIssuesInboundProcessingReasonCodes.TargetRelationInvalid,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return Failed(envelope);
        }

        if (source is null)
        {
            return await RejectAsync(
                    envelope,
                    GitHubIssuesInboundProcessingReasonCodes.TargetHasNoSuperior,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var mapping = new GitHubIssuesInboundDirectiveMapper(
                instance,
                envelope.Repository,
                source,
                envelope.CapturedAtUtc,
                correlation)
            .Map(parsed.Message!);
        if (!mapping.IsSuccess || mapping.Message is not Directive directive)
        {
            return await RejectAsync(
                    envelope,
                    GitHubIssuesInboundProcessingReasonCodes.MappingFailed,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var routing = await routingValidator
                .ValidateAsync(directive, cancellationToken)
                .ConfigureAwait(false);
            if (!routing.IsValid)
            {
                return await RejectAsync(
                        envelope,
                        GitHubIssuesInboundProcessingReasonCodes.RoutingRejected,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await submissionSink
                .SubmitAsync(directive, cancellationToken)
                .ConfigureAwait(false);
            return await CompleteAsync(
                    envelope,
                    new GitHubIssuesInboundCompletion(
                        GitHubIssuesInboundCompletionState.Submitted,
                        UtcNow(),
                        submission: new GitHubIssueSubmissionCorrelation(
                            correlation,
                            directive.DirectiveId)),
                    GitHubIssuesInboundProcessingStatus.Submitted,
                    reasonCode: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failed(envelope);
        }
    }

    private async Task<GitHubIssuesInboundProcessingResult> RejectAsync(
        GitHubIssuesInboundEnvelope envelope,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CompleteAsync(
                    envelope,
                    new GitHubIssuesInboundCompletion(
                        GitHubIssuesInboundCompletionState.Rejected,
                        UtcNow(),
                        reasonCode),
                    GitHubIssuesInboundProcessingStatus.Rejected,
                    reasonCode,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failed(envelope);
        }
    }

    private async Task<GitHubIssuesInboundProcessingResult> CompleteAsync(
        GitHubIssuesInboundEnvelope envelope,
        GitHubIssuesInboundCompletion completion,
        GitHubIssuesInboundProcessingStatus status,
        string? reasonCode,
        CancellationToken cancellationToken)
    {
        var completed = await store
            .TryCompleteAsync(envelope, completion, cancellationToken)
            .ConfigureAwait(false);
        return completed
            ? new GitHubIssuesInboundProcessingResult(
                envelope.InstanceId,
                envelope.Repository,
                envelope.ExternalEventId,
                status,
                reasonCode)
            : new GitHubIssuesInboundProcessingResult(
                envelope.InstanceId,
                envelope.Repository,
                envelope.ExternalEventId,
                GitHubIssuesInboundProcessingStatus.CompletionConflict,
                GitHubIssuesInboundProcessingReasonCodes.CompletionConflict);
    }

    private DateTimeOffset UtcNow() => timeProvider.GetUtcNow().ToUniversalTime();

    private static GitHubIssuesInboundProcessingResult Failed(
        GitHubIssuesInboundEnvelope envelope) =>
        new(
            envelope.InstanceId,
            envelope.Repository,
            envelope.ExternalEventId,
            GitHubIssuesInboundProcessingStatus.Failed,
            GitHubIssuesInboundProcessingReasonCodes.ProcessingFailed);
}

internal sealed class NoopGitHubIssuesInboundProcessor : IGitHubIssuesInboundProcessor
{
    public static NoopGitHubIssuesInboundProcessor Instance { get; } = new();

    private NoopGitHubIssuesInboundProcessor()
    {
    }

    public Task<GitHubIssuesInboundProcessingCycleResult> ProcessPendingAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GitHubIssuesInboundProcessingCycleResult([]));
}
