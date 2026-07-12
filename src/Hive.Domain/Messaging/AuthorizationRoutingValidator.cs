using System.Collections.Immutable;
using Hive.Domain.Governance;
using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

/// <summary>
/// Correlates authorization grants and denials with the original gate escalation and validates the
/// reverse governance route, single-use constraint and grant expiry (US-F0-12-T04).
/// </summary>
public sealed class AuthorizationRoutingValidator
{
    private static readonly ImmutableHashSet<Type> GrantFromTypes =
        MessageRoutingRules.For<AuthorizationGrant>().Paths
            .Select(path => path.FromEndpointType)
            .ToImmutableHashSet();

    private static readonly ImmutableHashSet<Type> GrantToTypes =
        MessageRoutingRules.For<AuthorizationGrant>().Paths
            .Select(path => path.ToEndpointType)
            .ToImmutableHashSet();

    private static readonly ImmutableHashSet<Type> DenialFromTypes =
        MessageRoutingRules.For<AuthorizationDenial>().Paths
            .Select(path => path.FromEndpointType)
            .ToImmutableHashSet();

    private static readonly ImmutableHashSet<Type> DenialToTypes =
        MessageRoutingRules.For<AuthorizationDenial>().Paths
            .Select(path => path.ToEndpointType)
            .ToImmutableHashSet();

    private readonly IAuthorizationEscalationLog _escalationLog;
    private readonly TimeProvider _timeProvider;

    public AuthorizationRoutingValidator(
        IAuthorizationEscalationLog escalationLog,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(escalationLog);
        _escalationLog = escalationLog;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<ValidationResult> ValidateAsync(
        AuthorizationGrant grant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return ValidateAsync(
            grant,
            grant.InReplyTo,
            grant.RetainedActionId,
            GrantFromTypes,
            GrantToTypes,
            grant.ExpiresAt,
            cancellationToken);
    }

    public ValueTask<ValidationResult> ValidateAsync(
        AuthorizationDenial denial,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(denial);
        return ValidateAsync(
            denial,
            denial.InReplyTo,
            denial.RetainedActionId,
            DenialFromTypes,
            DenialToTypes,
            expiresAt: null,
            cancellationToken);
    }

    private async ValueTask<ValidationResult> ValidateAsync(
        OrgMessage message,
        MessageId inReplyTo,
        RetainedActionId retainedActionId,
        ImmutableHashSet<Type> fromTypes,
        ImmutableHashSet<Type> toTypes,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var shapeErrors = new List<ValidationError>();
        RequireEndpoint(message.From, fromTypes, "from", shapeErrors);
        RequireEndpoint(message.To, toTypes, "to", shapeErrors);
        if (shapeErrors.Count != 0)
        {
            return ValidationResult.Create(shapeErrors);
        }

        var record = await _escalationLog.FindEscalationAsync(
            message.OrganizationId,
            inReplyTo,
            cancellationToken);
        if (record is null)
        {
            return ValidationResult.Create([AuthorizationValidationCatalog.EscalationNotFound()]);
        }

        var errors = new List<ValidationError>();
        if (message.OrganizationId != record.OrganizationId)
        {
            errors.Add(AuthorizationValidationCatalog.OrganizationMismatch());
        }

        if (message.Thread != record.Thread)
        {
            errors.Add(AuthorizationValidationCatalog.ThreadMismatch());
        }

        if (retainedActionId != record.RetainedActionId)
        {
            errors.Add(AuthorizationValidationCatalog.RetainedActionMismatch());
        }

        if (message.From != record.Recipient)
        {
            errors.Add(AuthorizationValidationCatalog.UnauthorizedIssuer());
        }

        if (message.To != record.Requester)
        {
            errors.Add(AuthorizationValidationCatalog.OriginalRequesterRequired());
        }

        if (record.IsResolved)
        {
            errors.Add(AuthorizationValidationCatalog.ResponseDuplicate());
        }

        if (expiresAt is { } expiry && expiry <= _timeProvider.GetUtcNow())
        {
            errors.Add(AuthorizationValidationCatalog.GrantExpired());
        }

        return ValidationResult.Create(errors);
    }

    private static void RequireEndpoint(
        EndpointRef endpoint,
        ImmutableHashSet<Type> allowedTypes,
        string path,
        ICollection<ValidationError> errors)
    {
        if (!allowedTypes.Contains(endpoint.GetType()))
        {
            errors.Add(AuthorizationValidationCatalog.EndpointNotAllowed(path));
        }
    }
}
