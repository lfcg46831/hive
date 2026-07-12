using System.Collections;
using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

internal static class MessageStructuralValidator
{
    public static ValidationResult Validate(
        OrgMessage message,
        MessageContractRule rule)
    {
        var errors = new List<ValidationError>();

        ValidateRequiredFields(message, rule, errors);
        ValidateClosedValues(message, rule, errors);
        ValidateEndpoint(message.From, rule.From, "from", errors);
        ValidateEndpoint(message.To, rule.To, "to", errors);
        ValidatePolicy(message, errors);

        return ValidationResult.Create(errors);
    }

    private static void ValidateRequiredFields(
        OrgMessage message,
        MessageContractRule rule,
        ICollection<ValidationError> errors)
    {
        foreach (var propertyName in rule.RequiredFields)
        {
            var property = message.GetType().GetProperty(propertyName);

            if (property is null)
            {
                throw new InvalidOperationException(
                    $"Required property {propertyName} does not exist on {message.GetType().Name}.");
            }

            var value = property.GetValue(message);
            var path = PathFor(propertyName);

            if (IsMissing(value))
            {
                errors.Add(Error("required-field", path));
                continue;
            }

            if (value is ApprovalPolicyRef)
            {
                continue;
            }

            if (property.PropertyType.Namespace == typeof(OrganizationId).Namespace
                && !HasValidIdentityValue(value!))
            {
                errors.Add(Error("invalid-identity", path));
            }
        }
    }

    private static void ValidateClosedValues(
        OrgMessage message,
        MessageContractRule rule,
        ICollection<ValidationError> errors)
    {
        if (!Enum.IsDefined(message.Priority))
        {
            errors.Add(Error("invalid-priority", "priority"));
        }

        if (!Enum.IsDefined(message.Channel) || message.Channel != rule.Channel)
        {
            errors.Add(Error("channel-mismatch", "channel"));
        }

        if (message is Report report && !Enum.IsDefined(report.Kind))
        {
            errors.Add(Error("invalid-report-kind", "kind"));
        }

        if (message is AuthorizationGrant grant
            && grant.ExpiresAt != default
            && grant.ExpiresAt <= grant.SentAt)
        {
            errors.Add(Error("invalid-expiration", "expiresAt"));
        }

        ValidateSystemEndpointKind(message.From, "from", errors);
        ValidateSystemEndpointKind(message.To, "to", errors);
    }

    private static void ValidateSystemEndpointKind(
        EndpointRef? endpoint,
        string path,
        ICollection<ValidationError> errors)
    {
        if (endpoint is SystemEndpointRef system && !Enum.IsDefined(system.Kind))
        {
            errors.Add(Error("invalid-system-endpoint", path));
        }
    }

    private static void ValidateEndpoint(
        EndpointRef? endpoint,
        IEnumerable<EndpointVariantRule> allowed,
        string path,
        ICollection<ValidationError> errors)
    {
        if (endpoint is null)
        {
            return;
        }

        if (endpoint is PositionEndpointRef position)
        {
            if (position.PositionId is null)
            {
                errors.Add(Error("required-field", $"{path}.positionId"));
                return;
            }

            if (!HasValidIdentityValue(position.PositionId))
            {
                errors.Add(Error("invalid-identity", $"{path}.positionId"));
                return;
            }
        }

        if (endpoint is SystemEndpointRef system && !Enum.IsDefined(system.Kind))
        {
            return;
        }

        if (!allowed.Any(rule => MatchesEndpointRule(endpoint, rule)))
        {
            errors.Add(new ValidationError(
                "endpoint-not-allowed",
                path,
                RejectionReason.InvalidRoute));
        }
    }

    private static bool MatchesEndpointRule(
        EndpointRef endpoint,
        EndpointVariantRule rule)
    {
        if (endpoint.GetType() != rule.EndpointType)
        {
            return false;
        }

        return endpoint is not SystemEndpointRef system || system.Kind == rule.SystemKind;
    }

    private static void ValidatePolicy(
        OrgMessage message,
        ICollection<ValidationError> errors)
    {
        if (message is not ApprovalRequest { Policy: not null } request)
        {
            return;
        }

        var value = request.Policy.Value;

        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            errors.Add(Error("invalid-policy", "policy.value"));
        }
    }

    private static bool IsMissing(object? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value is string text)
        {
            return string.IsNullOrWhiteSpace(text);
        }

        if (value is DateTimeOffset timestamp)
        {
            return timestamp == default;
        }

        if (value is IEnumerable values)
        {
            var enumerator = values.GetEnumerator();

            try
            {
                return !enumerator.MoveNext();
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        return false;
    }

    private static bool HasValidIdentityValue(object identity)
    {
        var value = identity.GetType().GetProperty("Value")?.GetValue(identity);

        return value switch
        {
            Guid guid => guid != Guid.Empty,
            string text => !string.IsNullOrWhiteSpace(text)
                && string.Equals(text, text.Trim(), StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string PathFor(string propertyName) =>
        char.ToLowerInvariant(propertyName[0]) + propertyName[1..];

    private static ValidationError Error(string code, string path) =>
        new(code, path, RejectionReason.InvalidContract);
}
