using System.Collections.Immutable;
using System.Text;
using Hive.Domain.Outcomes;

namespace Hive.Actors.Positions;

internal static class AiDirectiveOutcomeEvidenceContext
{
    public static OutcomeProposalEvidenceContext CreateProposalContext(
        AiDirectiveExecutionContext context) =>
        new(CreateVerificationEntries(context).Select(entry => entry.Reference));

    public static ImmutableArray<OutcomeVerificationContextEntry> CreateVerificationEntries(
        AiDirectiveExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var entries = ImmutableArray.CreateBuilder<OutcomeVerificationContextEntry>();
        var bytes = 0;
        AddIfBounded(
            entries,
            "directive.objective",
            context.Directive.Objective,
            ref bytes);
        AddIfBounded(
            entries,
            "directive.context",
            context.Directive.Context,
            ref bytes);
        return entries.ToImmutable();
    }

    private static void AddIfBounded(
        ICollection<OutcomeVerificationContextEntry> entries,
        string reference,
        string value,
        ref int bytes)
    {
        var entryBytes = Encoding.UTF8.GetByteCount(reference) +
            Encoding.UTF8.GetByteCount(value);
        if (bytes + entryBytes > OutcomeVerificationContext.MaximumUtf8Bytes)
        {
            return;
        }

        entries.Add(new OutcomeVerificationContextEntry(reference, value));
        bytes += entryBytes;
    }
}
