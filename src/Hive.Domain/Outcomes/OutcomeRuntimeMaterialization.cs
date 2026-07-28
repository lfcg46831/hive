using System.Collections.Immutable;

namespace Hive.Domain.Outcomes;

public enum OutcomeActionGateState
{
    NotRequired = 1,
    Authorized = 2,
    HumanApprovalRequired = 3,
    Denied = 4,
    Unknown = 5,
}

public enum OutcomeDependencyResultState
{
    Succeeded = 1,
    TransientFailure = 2,
    PermanentFailure = 3,
}

public enum OutcomeRequirementEvidenceState
{
    Satisfied = 1,
    NotSatisfied = 2,
    Unknown = 3,
}

public sealed record OutcomeDependencyResultFact
{
    public OutcomeDependencyResultFact(string reference, OutcomeDependencyResultState state)
    {
        Reference = OutcomeContractGuards.RequireReference(reference, nameof(reference));
        State = OutcomeContractGuards.RequireDefined(state, nameof(state));
    }

    public string Reference { get; }

    public OutcomeDependencyResultState State { get; }
}

public sealed record OutcomeRequirementEvidence
{
    public OutcomeRequirementEvidence(string reference, OutcomeRequirementEvidenceState state)
    {
        Reference = OutcomeContractGuards.RequireReference(reference, nameof(reference));
        State = OutcomeContractGuards.RequireDefined(state, nameof(state));
    }

    public string Reference { get; }

    public OutcomeRequirementEvidenceState State { get; }
}

/// <summary>
/// Immutable, provider-neutral snapshot of facts owned by the runtime. It deliberately contains
/// no model proposal or free-form model output.
/// </summary>
public sealed record OutcomeRuntimeSnapshot
{
    public OutcomeRuntimeSnapshot(
        int iterationCount,
        int retryCount,
        DateTimeOffset observedAt,
        DateTimeOffset? deadline,
        bool hasAvailableBudget,
        OutcomeActionGateState actionGateState,
        bool approvalPending,
        OutcomeRoutingState routingState,
        bool autonomousActionAvailable,
        bool delegationRequired,
        bool pendingActions,
        bool externalInterventionRequired,
        bool verifiableProgress,
        bool responsibilityRetained,
        IEnumerable<OutcomeDependencyResultFact>? dependencyResults = null,
        IEnumerable<OutcomeRequirementEvidence>? requirementEvidence = null,
        IEnumerable<OutcomePolicyTrigger>? observedPolicyTriggers = null)
    {
        if (iterationCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterationCount),
                iterationCount,
                "Iteration count cannot be negative.");
        }

        if (retryCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryCount),
                retryCount,
                "Retry count cannot be negative.");
        }

        if (observedAt == default)
        {
            throw new ArgumentException(
                "Runtime observation time must be specified.",
                nameof(observedAt));
        }

        if (deadline is { } deadlineValue && deadlineValue == default)
        {
            throw new ArgumentException(
                "Runtime deadline cannot be the default timestamp.",
                nameof(deadline));
        }

        ActionGateState = OutcomeContractGuards.RequireDefined(
            actionGateState,
            nameof(actionGateState));
        if (approvalPending && actionGateState != OutcomeActionGateState.HumanApprovalRequired)
        {
            throw new ArgumentException(
                "A pending approval requires a human-approval action gate.",
                nameof(approvalPending));
        }

        if (autonomousActionAvailable && delegationRequired)
        {
            throw new ArgumentException(
                "An action cannot be both autonomous work and delegated work.",
                nameof(delegationRequired));
        }

        IterationCount = iterationCount;
        RetryCount = retryCount;
        ObservedAt = observedAt;
        Deadline = deadline;
        HasAvailableBudget = hasAvailableBudget;
        ApprovalPending = approvalPending;
        RoutingState = OutcomeContractGuards.RequireDefined(routingState, nameof(routingState));
        AutonomousActionAvailable = autonomousActionAvailable;
        DelegationRequired = delegationRequired;
        PendingActions = pendingActions;
        ExternalInterventionRequired = externalInterventionRequired;
        VerifiableProgress = verifiableProgress;
        ResponsibilityRetained = responsibilityRetained;
        DependencyResults = SnapshotByReference(dependencyResults, nameof(dependencyResults));
        RequirementEvidence = SnapshotByReference(
            requirementEvidence,
            nameof(requirementEvidence));
        ObservedPolicyTriggers = OutcomeContractGuards.SnapshotDefinedDistinct(
            observedPolicyTriggers,
            nameof(observedPolicyTriggers));
    }

    public int IterationCount { get; }

    public int RetryCount { get; }

    public DateTimeOffset ObservedAt { get; }

    public DateTimeOffset? Deadline { get; }

    public bool HasAvailableBudget { get; }

    public OutcomeActionGateState ActionGateState { get; }

    public bool ApprovalPending { get; }

    public OutcomeRoutingState RoutingState { get; }

    public bool AutonomousActionAvailable { get; }

    public bool DelegationRequired { get; }

    public bool PendingActions { get; }

    public bool ExternalInterventionRequired { get; }

    public bool VerifiableProgress { get; }

    public bool ResponsibilityRetained { get; }

    public ImmutableArray<OutcomeDependencyResultFact> DependencyResults { get; }

    public ImmutableArray<OutcomeRequirementEvidence> RequirementEvidence { get; }

    public ImmutableArray<OutcomePolicyTrigger> ObservedPolicyTriggers { get; }

    private static ImmutableArray<T> SnapshotByReference<T>(
        IEnumerable<T>? source,
        string parameterName)
        where T : class
    {
        if (source is null)
        {
            return [];
        }

        var snapshot = source.ToImmutableArray();
        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException("Collection cannot contain null entries.", parameterName);
        }

        var references = snapshot.Select(item => item switch
        {
            OutcomeDependencyResultFact dependency => dependency.Reference,
            OutcomeRequirementEvidence evidence => evidence.Reference,
            _ => throw new InvalidOperationException(
                $"Unsupported runtime fact type '{typeof(T).Name}'."),
        });
        if (references.Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("Fact references must be unique.", parameterName);
        }

        return snapshot;
    }
}

public interface IExecutionFactsMaterializer
{
    ExecutionFacts Materialize(
        OutcomeRuntimeSnapshot runtime,
        DirectiveExecutionContract directive);
}

/// <summary>Projects authoritative runtime state into the closed execution-facts contract.</summary>
public sealed class ExecutionFactsMaterializer : IExecutionFactsMaterializer
{
    public ExecutionFacts Materialize(
        OutcomeRuntimeSnapshot runtime,
        DirectiveExecutionContract directive)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(directive);

        var evidence = RequireKnownEvidence(runtime.RequirementEvidence, directive);

        return new ExecutionFacts(
            runtime.IterationCount,
            runtime.RetryCount,
            deadlineExceeded: runtime.Deadline is { } deadline && runtime.ObservedAt >= deadline,
            budgetExhausted: !runtime.HasAvailableBudget,
            humanApprovalRequired:
                runtime.ActionGateState == OutcomeActionGateState.HumanApprovalRequired,
            approvalPending: runtime.ApprovalPending,
            dependencyState: MaterializeDependencyState(runtime.DependencyResults),
            authorityState: MaterializeAuthorityState(runtime.ActionGateState),
            routingState: runtime.RoutingState,
            autonomousActionAvailable: runtime.AutonomousActionAvailable,
            delegationRequired: runtime.DelegationRequired,
            pendingActions: runtime.PendingActions,
            externalInterventionRequired: runtime.ExternalInterventionRequired,
            verifiableProgress: runtime.VerifiableProgress,
            responsibilityRetained: runtime.ResponsibilityRetained,
            completionState: MaterializeCompletionState(directive, evidence),
            runtime.ObservedPolicyTriggers);
    }

    private static OutcomeDependencyState MaterializeDependencyState(
        IEnumerable<OutcomeDependencyResultFact> dependencyResults)
    {
        var states = dependencyResults.Select(result => result.State).ToImmutableArray();
        if (states.Contains(OutcomeDependencyResultState.PermanentFailure))
        {
            return OutcomeDependencyState.PermanentFailure;
        }

        return states.Contains(OutcomeDependencyResultState.TransientFailure)
            ? OutcomeDependencyState.TransientFailure
            : OutcomeDependencyState.Available;
    }

    private static OutcomeAuthorityState MaterializeAuthorityState(
        OutcomeActionGateState actionGateState) =>
        actionGateState switch
        {
            OutcomeActionGateState.NotRequired => OutcomeAuthorityState.NotRequired,
            OutcomeActionGateState.Authorized => OutcomeAuthorityState.Authorized,
            OutcomeActionGateState.HumanApprovalRequired => OutcomeAuthorityState.NotRequired,
            OutcomeActionGateState.Denied => OutcomeAuthorityState.Denied,
            OutcomeActionGateState.Unknown => OutcomeAuthorityState.Unknown,
            _ => throw new ArgumentOutOfRangeException(
                nameof(actionGateState),
                actionGateState,
                "Unknown action-gate state."),
        };

    private static IReadOnlyDictionary<string, OutcomeRequirementEvidenceState> RequireKnownEvidence(
        IEnumerable<OutcomeRequirementEvidence> evidence,
        DirectiveExecutionContract directive)
    {
        var allowed = directive.RequiredInputs
            .Concat(directive.CompletionCriteria)
            .Select(requirement => requirement.Reference)
            .ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, OutcomeRequirementEvidenceState>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            if (!allowed.Contains(item.Reference))
            {
                throw new ArgumentException(
                    $"Evidence reference '{item.Reference}' is not declared by the directive.",
                    nameof(evidence));
            }

            result.Add(item.Reference, item.State);
        }

        return result;
    }

    private static OutcomeCompletionState MaterializeCompletionState(
        DirectiveExecutionContract directive,
        IReadOnlyDictionary<string, OutcomeRequirementEvidenceState> evidence)
    {
        if (directive.CompletionCriteria.IsEmpty)
        {
            return OutcomeCompletionState.NotDeclared;
        }

        var inputStates = directive.RequiredInputs
            .Select(requirement => StateFor(requirement.Reference, evidence));
        var criterionStates = directive.CompletionCriteria
            .Select(requirement => StateFor(requirement.Reference, evidence));
        var states = inputStates.Concat(criterionStates).ToImmutableArray();

        if (states.Contains(OutcomeRequirementEvidenceState.NotSatisfied))
        {
            return OutcomeCompletionState.NotSatisfied;
        }

        return states.All(state => state == OutcomeRequirementEvidenceState.Satisfied)
            ? OutcomeCompletionState.Satisfied
            : OutcomeCompletionState.Unknown;
    }

    private static OutcomeRequirementEvidenceState StateFor(
        string reference,
        IReadOnlyDictionary<string, OutcomeRequirementEvidenceState> evidence) =>
        evidence.TryGetValue(reference, out var state)
            ? state
            : OutcomeRequirementEvidenceState.Unknown;
}
