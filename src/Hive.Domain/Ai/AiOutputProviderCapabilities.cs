using System.Collections.Immutable;

namespace Hive.Domain.Ai;

public sealed record AiOutputProviderCapabilities
{
    public AiOutputProviderCapabilities(IEnumerable<AiOutputConstraintMode>? supportedModes)
    {
        if (supportedModes is null)
        {
            SupportedModes = [];
            return;
        }

        var builder = ImmutableArray.CreateBuilder<AiOutputConstraintMode>();
        foreach (var mode in supportedModes)
        {
            var defined = AiOutputConstraintModeContract.RequireDefined(
                mode,
                nameof(supportedModes));
            if (!builder.Contains(defined))
            {
                builder.Add(defined);
            }
        }

        SupportedModes = builder.ToImmutable();
    }

    public ImmutableArray<AiOutputConstraintMode> SupportedModes { get; }

    public bool Supports(AiOutputConstraintMode mode) =>
        SupportedModes.Contains(
            AiOutputConstraintModeContract.RequireDefined(mode, nameof(mode)));
}
