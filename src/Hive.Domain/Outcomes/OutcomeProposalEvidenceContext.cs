using System.Collections.Immutable;

namespace Hive.Domain.Outcomes;

/// <summary>
/// Provider-neutral evidence vocabulary available to an OutcomeProposal parser/constraint for
/// one bounded inference call. Semantic-completion evidence can only cite exact DirectiveInput
/// references present in this context.
/// </summary>
public sealed record OutcomeProposalEvidenceContext
{
    public const int MaximumReferences = OutcomeVerificationContext.MaximumEntries;

    public OutcomeProposalEvidenceContext(IEnumerable<string>? directiveInputReferences)
    {
        var snapshot = (directiveInputReferences ?? [])
            .Select(reference => OutcomeContractGuards.RequireReference(
                reference,
                nameof(directiveInputReferences)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reference => reference, StringComparer.Ordinal)
            .ToImmutableArray();
        if (snapshot.Length > MaximumReferences)
        {
            throw new ArgumentException(
                $"Outcome proposal evidence context cannot contain more than {MaximumReferences} references.",
                nameof(directiveInputReferences));
        }

        DirectiveInputReferences = snapshot;
    }

    public ImmutableArray<string> DirectiveInputReferences { get; }

    public bool Allows(OutcomeEvidenceReference evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        return evidence.Source == OutcomeEvidenceSource.DirectiveInput &&
            DirectiveInputReferences.Contains(evidence.Reference, StringComparer.Ordinal);
    }
}
