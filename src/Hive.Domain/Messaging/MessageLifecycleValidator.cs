namespace Hive.Domain.Messaging;

internal static class MessageLifecycleValidator
{
    public static ValidationResult Validate(
        MessageState from,
        MessageState to,
        RejectionReason? rejectionReason)
    {
        var errors = new List<ValidationError>();
        var fromIsDefined = Enum.IsDefined(from);
        var toIsDefined = Enum.IsDefined(to);

        if (!fromIsDefined)
        {
            errors.Add(Error("invalid-state", "state.from"));
        }

        if (!toIsDefined)
        {
            errors.Add(Error("invalid-state", "state.to"));
        }

        if (fromIsDefined
            && toIsDefined
            && !MessageStateContract.CanTransition(from, to))
        {
            errors.Add(Error("invalid-state-transition", "state"));
        }

        if (rejectionReason is null)
        {
            if (toIsDefined && to == MessageState.Rejected)
            {
                errors.Add(Error("rejection-reason-required", "rejectionReason"));
            }
        }
        else if (!Enum.IsDefined(rejectionReason.Value))
        {
            errors.Add(Error("invalid-rejection-reason", "rejectionReason"));
        }
        else if (toIsDefined && to != MessageState.Rejected)
        {
            errors.Add(Error("rejection-reason-not-allowed", "rejectionReason"));
        }

        return ValidationResult.Create(errors);
    }

    private static ValidationError Error(string code, string path) =>
        new(code, path, RejectionReason.InvalidContract);
}
