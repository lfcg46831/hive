using System.Collections.Immutable;

namespace Hive.Domain.Governance;

/// <summary>The closed outcome of evaluating an action against the authority gate.</summary>
public enum ActionGateOutcome
{
    Allowed = 1,
    EscalationRequired = 2,
    HumanApprovalRequired = 3,
}

/// <summary>The deterministic branch that produced an <see cref="ActionGateResolution"/>.</summary>
public enum ActionGateResolutionReason
{
    DeclaredAuthority = 1,
    ObjectiveEscalation = 2,
    ObjectiveHumanApproval = 3,
    UnmatchedActionDefault = 4,
}

/// <summary>One objective action-domain match and the gate effective for this position.</summary>
public sealed record ActionGateMatch
{
    public ActionGateMatch(
        AuthorityKey key,
        ActionDomainGate minimumGate,
        ActionDomainGate effectiveGate)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        MinimumGate = ActionDomainCatalogGuards.RequireDefined(
            minimumGate,
            nameof(minimumGate));
        EffectiveGate = ActionDomainCatalogGuards.RequireDefined(
            effectiveGate,
            nameof(effectiveGate));

        if (EffectiveGate < MinimumGate)
        {
            throw new ArgumentException(
                "The effective gate cannot relax the catalog minimum.",
                nameof(effectiveGate));
        }
    }

    public AuthorityKey Key { get; }

    public ActionDomainGate MinimumGate { get; }

    public ActionDomainGate EffectiveGate { get; }
}

/// <summary>
/// One logical approval requirement. A missing approver preserves policy resolution by authority
/// key for T08; an explicit approver groups every key requiring that same approver.
/// </summary>
public sealed class ActionGateApprovalRequirement : IEquatable<ActionGateApprovalRequirement>
{
    public ActionGateApprovalRequirement(
        string? approver,
        IEnumerable<AuthorityKey> authorityKeys)
    {
        ArgumentNullException.ThrowIfNull(authorityKeys);

        Approver = approver is null
            ? null
            : ActionDomainCatalogGuards.RequireText(approver, nameof(approver));

        var keys = authorityKeys
            .Select(key => key ?? throw new ArgumentException(
                "Approval authority keys cannot contain null entries.",
                nameof(authorityKeys)))
            .Distinct()
            .OrderBy(key => key.Value, StringComparer.Ordinal)
            .ToImmutableArray();

        if (keys.IsEmpty)
        {
            throw new ArgumentException(
                "An approval requirement must contain at least one authority key.",
                nameof(authorityKeys));
        }

        AuthorityKeys = keys;
    }

    public string? Approver { get; }

    public ImmutableArray<AuthorityKey> AuthorityKeys { get; }

    public bool Equals(ActionGateApprovalRequirement? other) =>
        other is not null
        && string.Equals(Approver, other.Approver, StringComparison.Ordinal)
        && AuthorityKeys.SequenceEqual(other.AuthorityKeys);

    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj) || Equals(obj as ActionGateApprovalRequirement);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Approver, StringComparer.Ordinal);
        foreach (var key in AuthorityKeys)
        {
            hash.Add(key);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Immutable, structurally comparable result of the pure action-domain gate.</summary>
public sealed class ActionGateResolution : IEquatable<ActionGateResolution>
{
    public const string DeclaredAuthorityCode = "action-gate-declared-authority";
    public const string ObjectiveEscalationCode = "action-gate-objective-escalation";
    public const string ObjectiveHumanApprovalCode = "action-gate-objective-human-approval";
    public const string UnmatchedActionDefaultCode = "action-gate-unmatched-action-default";

    private ActionGateResolution(
        ActionGateOutcome outcome,
        ActionGateResolutionReason reason,
        ImmutableArray<ActionGateMatch> matches,
        AuthorityKey? allowedAuthorityKey,
        ImmutableArray<ActionGateApprovalRequirement> requiredApprovals)
    {
        Outcome = RequireDefined(outcome, nameof(outcome));
        Reason = RequireDefined(reason, nameof(reason));
        Matches = matches;
        AllowedAuthorityKey = allowedAuthorityKey;
        RequiredApprovals = requiredApprovals;
        Code = CodeFor(reason);

        ValidateShape();
    }

    public ActionGateOutcome Outcome { get; }

    public ActionGateResolutionReason Reason { get; }

    public string Code { get; }

    public ImmutableArray<ActionGateMatch> Matches { get; }

    public AuthorityKey? AllowedAuthorityKey { get; }

    public ImmutableArray<ActionGateApprovalRequirement> RequiredApprovals { get; }

    internal static ActionGateResolution Allowed(
        ImmutableArray<ActionGateMatch> matches,
        AuthorityKey authorityKey) =>
        new(
            ActionGateOutcome.Allowed,
            ActionGateResolutionReason.DeclaredAuthority,
            matches,
            authorityKey ?? throw new ArgumentNullException(nameof(authorityKey)),
            []);

    internal static ActionGateResolution Escalation(
        ImmutableArray<ActionGateMatch> matches,
        ActionGateResolutionReason reason) =>
        new(
            ActionGateOutcome.EscalationRequired,
            reason,
            matches,
            allowedAuthorityKey: null,
            []);

    internal static ActionGateResolution HumanApproval(
        ImmutableArray<ActionGateMatch> matches,
        ImmutableArray<ActionGateApprovalRequirement> requiredApprovals) =>
        new(
            ActionGateOutcome.HumanApprovalRequired,
            ActionGateResolutionReason.ObjectiveHumanApproval,
            matches,
            allowedAuthorityKey: null,
            requiredApprovals);

    public bool Equals(ActionGateResolution? other) =>
        other is not null
        && Outcome == other.Outcome
        && Reason == other.Reason
        && string.Equals(Code, other.Code, StringComparison.Ordinal)
        && Equals(AllowedAuthorityKey, other.AllowedAuthorityKey)
        && Matches.SequenceEqual(other.Matches)
        && RequiredApprovals.SequenceEqual(other.RequiredApprovals);

    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj) || Equals(obj as ActionGateResolution);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Outcome);
        hash.Add(Reason);
        hash.Add(Code, StringComparer.Ordinal);
        hash.Add(AllowedAuthorityKey);
        foreach (var match in Matches)
        {
            hash.Add(match);
        }

        foreach (var approval in RequiredApprovals)
        {
            hash.Add(approval);
        }

        return hash.ToHashCode();
    }

    private void ValidateShape()
    {
        var isValid = Outcome switch
        {
            ActionGateOutcome.Allowed =>
                Reason == ActionGateResolutionReason.DeclaredAuthority
                && AllowedAuthorityKey is not null
                && RequiredApprovals.IsEmpty,
            ActionGateOutcome.EscalationRequired =>
                Reason is ActionGateResolutionReason.ObjectiveEscalation
                    or ActionGateResolutionReason.UnmatchedActionDefault
                && AllowedAuthorityKey is null
                && RequiredApprovals.IsEmpty,
            ActionGateOutcome.HumanApprovalRequired =>
                Reason == ActionGateResolutionReason.ObjectiveHumanApproval
                && AllowedAuthorityKey is null
                && !RequiredApprovals.IsEmpty,
            _ => false,
        };

        if (!isValid)
        {
            throw new ArgumentException("The action gate result shape does not match its outcome.");
        }
    }

    private static string CodeFor(ActionGateResolutionReason reason) =>
        reason switch
        {
            ActionGateResolutionReason.DeclaredAuthority => DeclaredAuthorityCode,
            ActionGateResolutionReason.ObjectiveEscalation => ObjectiveEscalationCode,
            ActionGateResolutionReason.ObjectiveHumanApproval => ObjectiveHumanApprovalCode,
            ActionGateResolutionReason.UnmatchedActionDefault => UnmatchedActionDefaultCode,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown gate reason."),
        };

    private static TEnum RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Unknown action gate value.");
        }

        return value;
    }
}

/// <summary>Pure deterministic resolver for the authority algorithm defined by bible section 4.9.</summary>
public static class ActionGateResolver
{
    public static ActionGateResolution Resolve(
        ActionDomainCatalog catalog,
        ActionDomainAuthorityBinding authority,
        ActionFacts facts,
        ActingUnderDeclaration actingUnder)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(actingUnder);

        RequireFailClosedDefault(catalog);
        RequireCanonicalPredicateValues(catalog);

        var evaluatedMatches = catalog.Domains
            .Where(domain => domain.Match.Count > 0
                             && domain.Match.Any(predicate => Matches(predicate, facts)))
            .Select(domain => Evaluate(domain, authority.Overrides))
            .OrderBy(match => match.Match.Key.Value, StringComparer.Ordinal)
            .ToImmutableArray();

        var matches = evaluatedMatches
            .Select(match => match.Match)
            .ToImmutableArray();

        var humanApprovalMatches = evaluatedMatches
            .Where(match => match.Match.EffectiveGate == ActionDomainGate.HumanApproval)
            .ToImmutableArray();
        if (!humanApprovalMatches.IsEmpty)
        {
            return ActionGateResolution.HumanApproval(
                matches,
                BuildApprovalRequirements(humanApprovalMatches));
        }

        if (evaluatedMatches.Any(
                match => match.Match.EffectiveGate == ActionDomainGate.Escalate))
        {
            return ActionGateResolution.Escalation(
                matches,
                ActionGateResolutionReason.ObjectiveEscalation);
        }

        var allowedKey = ResolveAllowedAuthorityKey(catalog, authority, actingUnder);
        if (allowedKey is not null)
        {
            return ActionGateResolution.Allowed(matches, allowedKey);
        }

        return ActionGateResolution.Escalation(
            matches,
            ActionGateResolutionReason.UnmatchedActionDefault);
    }

    private static EvaluatedActionGateMatch Evaluate(
        ActionDomain domain,
        IReadOnlyList<ActionDomainAuthorityOverride> overrides)
    {
        var matchingOverrides = overrides
            .Where(authorityOverride => authorityOverride.Key == domain.Key)
            .ToImmutableArray();
        var effectiveGate = matchingOverrides.Aggregate(
            domain.Gate,
            (current, authorityOverride) => MoreSevere(current, authorityOverride.Gate));
        var approvers = matchingOverrides
            .Where(authorityOverride => authorityOverride.Approver is not null)
            .Select(authorityOverride => authorityOverride.Approver!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(approver => approver, StringComparer.Ordinal)
            .ToImmutableArray();

        return new EvaluatedActionGateMatch(
            new ActionGateMatch(domain.Key, domain.Gate, effectiveGate),
            approvers);
    }

    private static bool Matches(
        ActionDomainMatchPredicate predicate,
        ActionFacts facts)
    {
        if (predicate.Action != facts.Action)
        {
            return false;
        }

        foreach (var (name, rawValue) in predicate.Attributes)
        {
            if (!ActionAttributeValue.TryFromScalar(rawValue, out var expected))
            {
                throw new ArgumentException(
                    $"Predicate attribute '{name}' is not a canonical scalar.",
                    nameof(predicate));
            }

            if (!facts.Attributes.TryGetValue(name, out var actual)
                || actual != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static ImmutableArray<ActionGateApprovalRequirement> BuildApprovalRequirements(
        ImmutableArray<EvaluatedActionGateMatch> humanApprovalMatches)
    {
        var requirements = ImmutableArray.CreateBuilder<ActionGateApprovalRequirement>();

        foreach (var match in humanApprovalMatches.Where(match => match.Approvers.IsEmpty))
        {
            requirements.Add(new ActionGateApprovalRequirement(
                approver: null,
                [match.Match.Key]));
        }

        foreach (var group in humanApprovalMatches
                     .SelectMany(match => match.Approvers.Select(
                         approver => new { Approver = approver, match.Match.Key }))
                     .GroupBy(item => item.Approver, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            requirements.Add(new ActionGateApprovalRequirement(
                group.Key,
                group.Select(item => item.Key)));
        }

        return requirements
            .OrderBy(requirement => requirement.Approver is null ? 0 : 1)
            .ThenBy(
                requirement => requirement.Approver
                               ?? requirement.AuthorityKeys[0].Value,
                StringComparer.Ordinal)
            .ThenBy(requirement => requirement.AuthorityKeys[0].Value, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static AuthorityKey? ResolveAllowedAuthorityKey(
        ActionDomainCatalog catalog,
        ActionDomainAuthorityBinding authority,
        ActingUnderDeclaration actingUnder)
    {
        if (actingUnder.State != ActingUnderDeclarationState.Declared)
        {
            return null;
        }

        var allowedKey = authority.CanDecide.FirstOrDefault(
            key => string.Equals(
                key.Value,
                actingUnder.Key!.Value,
                StringComparison.Ordinal));
        if (allowedKey is null)
        {
            return null;
        }

        var trustKeyExists = catalog.Domains.Any(domain =>
            domain.Key == allowedKey
            && domain.Match.Count == 0
            && domain.Gate == ActionDomainGate.Decide);

        return trustKeyExists ? allowedKey : null;
    }

    private static ActionDomainGate MoreSevere(
        ActionDomainGate left,
        ActionDomainGate right) =>
        left >= right ? left : right;

    private static void RequireFailClosedDefault(ActionDomainCatalog catalog)
    {
        if (catalog.Defaults.UnmatchedAction != ActionDomainGate.Escalate)
        {
            throw new ArgumentException(
                "The validated F0 catalog must default unmatched actions to escalation.",
                nameof(catalog));
        }
    }

    private static void RequireCanonicalPredicateValues(ActionDomainCatalog catalog)
    {
        foreach (var domain in catalog.Domains)
        {
            foreach (var predicate in domain.Match)
            {
                foreach (var (name, value) in predicate.Attributes)
                {
                    if (!ActionAttributeValue.TryFromScalar(value, out _))
                    {
                        throw new ArgumentException(
                            $"Predicate attribute '{name}' is not a canonical scalar.",
                            nameof(catalog));
                    }
                }
            }
        }
    }

    private sealed record EvaluatedActionGateMatch(
        ActionGateMatch Match,
        ImmutableArray<string> Approvers);
}
