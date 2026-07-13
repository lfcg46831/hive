using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

internal sealed record AiOutputConstraintNegotiation(
    AiOutputConstraintMode? EffectiveMode,
    string? FailureReason)
{
    public bool IsSuccess => FailureReason is null;

    public bool IsFailure => !IsSuccess;
}

internal static class AiOutputConstraintNegotiator
{
    public static AiOutputConstraintNegotiation Negotiate(
        AiOutputConstraint? constraint,
        AiOutputProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (constraint is null)
        {
            return new AiOutputConstraintNegotiation(
                EffectiveMode: null,
                FailureReason: null);
        }

        if (capabilities.Supports(AiOutputConstraintMode.JsonSchema))
        {
            return Success(AiOutputConstraintMode.JsonSchema);
        }

        if (constraint.AllowsFallback(AiOutputConstraintMode.JsonObject) &&
            capabilities.Supports(AiOutputConstraintMode.JsonObject))
        {
            return Success(AiOutputConstraintMode.JsonObject);
        }

        if (constraint.AllowsFallback(AiOutputConstraintMode.Text) &&
            capabilities.Supports(AiOutputConstraintMode.Text))
        {
            return Success(AiOutputConstraintMode.Text);
        }

        return new AiOutputConstraintNegotiation(
            EffectiveMode: null,
            "AI gateway provider does not support the requested output constraint or an explicitly allowed fallback.");
    }

    private static AiOutputConstraintNegotiation Success(AiOutputConstraintMode mode) =>
        new(mode, FailureReason: null);
}
