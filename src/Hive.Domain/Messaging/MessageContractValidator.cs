namespace Hive.Domain.Messaging;

public sealed class MessageContractValidator
{
    private const int InitialSchemaVersion = 1;

    public ValidationResult ValidateTransition(
        MessageState from,
        MessageState to,
        RejectionReason? rejectionReason) =>
        MessageLifecycleValidator.Validate(from, to, rejectionReason);

    public async ValueTask<ValidationResult> ValidateAsync(
        OrgMessage? message,
        IMessageValidationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (message is null)
        {
            return ValidationResult.Create(
            [
                new("materialization-failed", "$", RejectionReason.InvalidContract),
            ]);
        }

        if (message.SchemaVersion != InitialSchemaVersion)
        {
            var error = message.SchemaVersion <= 0
                ? new ValidationError(
                    "required-field",
                    "schemaVersion",
                    RejectionReason.InvalidContract)
                : new ValidationError(
                    "unsupported-schema-version",
                    "schemaVersion",
                    RejectionReason.UnsupportedSchemaVersion);

            return ValidationResult.Create([error]);
        }

        MessageContractRule rule;

        try
        {
            rule = MessageContractRules.For(message.GetType());
        }
        catch (ArgumentException)
        {
            return ValidationResult.Create(
            [
                new("unknown-message-type", "$", RejectionReason.InvalidContract),
            ]);
        }

        var structuralResult = MessageStructuralValidator.Validate(message, rule);

        if (!structuralResult.IsValid)
        {
            return structuralResult;
        }

        return await MessageReferenceValidator.ValidateAsync(
            message,
            rule,
            context,
            cancellationToken);
    }
}
