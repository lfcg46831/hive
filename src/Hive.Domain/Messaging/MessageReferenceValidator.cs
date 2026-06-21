using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

internal static class MessageReferenceValidator
{
    public static async ValueTask<ValidationResult> ValidateAsync(
        OrgMessage message,
        MessageContractRule rule,
        IMessageValidationContext context,
        CancellationToken cancellationToken)
    {
        var errors = new List<ValidationError>();

        foreach (var reference in rule.References)
        {
            var property = message.GetType().GetProperty(reference.SourceProperty)
                ?? throw new InvalidOperationException(
                    $"Reference property {reference.SourceProperty} does not exist on {message.GetType().Name}.");
            var source = property.GetValue(message) as DirectiveId;

            if (source is null)
            {
                continue;
            }

            var path = PathFor(reference.SourceProperty);

            if (reference.TargetMessageType != typeof(Directive))
            {
                throw new InvalidOperationException(
                    $"Unsupported reference target {reference.TargetMessageType.Name}.");
            }

            if (reference.DisallowSelfReference
                && message is Directive incoming
                && source == incoming.DirectiveId)
            {
                errors.Add(Error("self-reference", path));
                continue;
            }

            var target = await context.FindDirectiveAsync(source, cancellationToken);

            if (target is null)
            {
                errors.Add(Error("reference-not-found", path));
                continue;
            }

            if (!ValidateScope(message, target, reference, path, errors))
            {
                continue;
            }

            if (reference.DisallowCycles && message is Directive directive)
            {
                await ValidateCycleAsync(
                    directive,
                    target,
                    reference,
                    path,
                    context,
                    errors,
                    cancellationToken);
            }
        }

        return ValidationResult.Create(errors);
    }

    private static bool ValidateScope(
        OrgMessage message,
        Directive target,
        MessageReferenceRule reference,
        string path,
        ICollection<ValidationError> errors)
    {
        var isValid = true;

        if (reference.MustShareOrganization
            && target.OrganizationId != message.OrganizationId)
        {
            errors.Add(Error("reference-organization-mismatch", path));
            isValid = false;
        }

        if (reference.MustShareThread && target.Thread != message.Thread)
        {
            errors.Add(Error("reference-thread-mismatch", path));
            isValid = false;
        }

        return isValid;
    }

    private static async ValueTask ValidateCycleAsync(
        Directive incoming,
        Directive parent,
        MessageReferenceRule reference,
        string path,
        IMessageValidationContext context,
        ICollection<ValidationError> errors,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<DirectiveId> { incoming.DirectiveId };
        var current = parent;

        while (true)
        {
            if (!visited.Add(current.DirectiveId))
            {
                errors.Add(Error("reference-cycle", path));
                return;
            }

            if (!ValidateScope(incoming, current, reference, path, errors))
            {
                return;
            }

            if (current.ParentDirectiveId is not { } parentId)
            {
                return;
            }

            if (visited.Contains(parentId))
            {
                errors.Add(Error("reference-cycle", path));
                return;
            }

            var ancestor = await context.FindDirectiveAsync(parentId, cancellationToken);

            if (ancestor is null)
            {
                errors.Add(Error("reference-not-found", path));
                return;
            }

            current = ancestor;
        }
    }

    private static string PathFor(string propertyName) =>
        char.ToLowerInvariant(propertyName[0]) + propertyName[1..];

    private static ValidationError Error(string code, string path) =>
        new(code, path, RejectionReason.InvalidContract);
}
