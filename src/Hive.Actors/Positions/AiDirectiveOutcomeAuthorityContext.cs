using Hive.Domain.Outcomes;

namespace Hive.Actors.Positions;

internal static class AiDirectiveOutcomeAuthorityContext
{
    public static OutcomeProposalAuthorityContext CreateProposalContext(
        AiDirectiveExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Approval policies are resolved only after the action gate has evaluated a concrete
        // candidate. None are available at inference time, so that typed vocabulary remains
        // deliberately empty instead of deriving policy references from prose or key names.
        return new OutcomeProposalAuthorityContext(
            context.Authority.CanDecide.Concat(
                context.Authority.Overrides.Select(authorityOverride => authorityOverride.Key)));
    }
}
