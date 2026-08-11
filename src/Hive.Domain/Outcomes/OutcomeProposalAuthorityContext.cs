using System.Collections.Immutable;
using Hive.Domain.Governance;
using Hive.Domain.Identity;

namespace Hive.Domain.Outcomes;

/// <summary>
/// Immutable, provider-neutral authority vocabulary that is applicable to one outcome proposal.
/// References remain typed until the prompt/schema boundary so one authority kind cannot be
/// substituted for the other by matching string shape alone.
/// </summary>
public sealed record OutcomeProposalAuthorityContext
{
    public const int MaximumReferences = 32;

    public OutcomeProposalAuthorityContext(
        IEnumerable<AuthorityKey>? actionDomainReferences = null,
        IEnumerable<ApprovalPolicyRef>? approvalPolicyReferences = null)
    {
        ActionDomainReferences = Snapshot(
            actionDomainReferences,
            reference => reference.Value,
            nameof(actionDomainReferences));
        ApprovalPolicyReferences = Snapshot(
            approvalPolicyReferences,
            reference => reference.Value,
            nameof(approvalPolicyReferences));

        if (ActionDomainReferences.Length + ApprovalPolicyReferences.Length >
            MaximumReferences)
        {
            throw new ArgumentException(
                $"Outcome proposal authority context cannot contain more than {MaximumReferences} references.");
        }
    }

    public ImmutableArray<AuthorityKey> ActionDomainReferences { get; }

    public ImmutableArray<ApprovalPolicyRef> ApprovalPolicyReferences { get; }

    public bool HasReferences =>
        !ActionDomainReferences.IsEmpty || !ApprovalPolicyReferences.IsEmpty;

    public bool Allows(OutcomeAuthorityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.AuthorityKind switch
        {
            OutcomeAuthorityKind.ActionDomain => ActionDomainReferences.Any(reference =>
                string.Equals(
                    reference.Value,
                    request.AuthorityReference,
                    StringComparison.Ordinal)),
            OutcomeAuthorityKind.ApprovalPolicy => ApprovalPolicyReferences.Any(reference =>
                string.Equals(
                    reference.Value,
                    request.AuthorityReference,
                    StringComparison.Ordinal)),
            _ => false,
        };
    }

    public ImmutableArray<string> ReferencesFor(OutcomeAuthorityKind kind) =>
        OutcomeAuthorityKindContract.RequireDefined(kind, nameof(kind)) switch
        {
            OutcomeAuthorityKind.ActionDomain => ActionDomainReferences
                .Select(reference => reference.Value)
                .ToImmutableArray(),
            OutcomeAuthorityKind.ApprovalPolicy => ApprovalPolicyReferences
                .Select(reference => reference.Value)
                .ToImmutableArray(),
            _ => throw new InvalidOperationException("Validated authority kind is not mapped."),
        };

    private static ImmutableArray<T> Snapshot<T>(
        IEnumerable<T>? source,
        Func<T, string> value,
        string parameterName)
        where T : class
    {
        if (source is null)
        {
            return [];
        }

        var snapshot = source.ToArray();
        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException(
                "Authority context cannot contain null references.",
                parameterName);
        }

        return snapshot
            .Distinct()
            .OrderBy(value, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}
