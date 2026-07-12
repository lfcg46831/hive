using Hive.Domain.Identity;

namespace Hive.Domain.Governance;

/// <summary>
/// Read-only correlation seam over gate escalations that may be resolved by an authorization
/// message (US-F0-12-T04).
/// </summary>
public interface IAuthorizationEscalationLog
{
    /// <summary>
    /// Finds an escalation by organization and message identity. A missing record returns
    /// <see langword="null"/>; cancellation and technical failures remain exceptional.
    /// </summary>
    ValueTask<AuthorizationEscalationRecord?> FindEscalationAsync(
        OrganizationId organizationId,
        MessageId escalationId,
        CancellationToken cancellationToken = default);
}
