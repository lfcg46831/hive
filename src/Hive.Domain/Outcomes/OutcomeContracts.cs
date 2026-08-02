using System.Collections.Immutable;
using Hive.Domain.Governance;
using Hive.Domain.Identity;

namespace Hive.Domain.Outcomes;

public static class OrganizationalOutcomeContractVersions
{
    public const int ExecutionFacts = 2;
    public const int DirectiveExecution = 1;
    public const int OutcomeProposal = 3;
    public const int PolicySnapshot = 1;
    public const int OutcomeResolution = 4;
    public const int OutcomeVerification = 2;
    public const int OutcomeVerifierOutput = 1;
}

public enum OutcomeKind
{
    ContinueWork = 1,
    ReportProgress = 2,
    ReportDone = 3,
    Escalation = 4,
    Directive = 5,
    ApprovalRequired = 6,
    Undetermined = 7,
}

public static class OutcomeKindContract
{
    private static readonly OutcomeEnumWireContract<OutcomeKind> Contract = new(
        (OutcomeKind.ContinueWork, "ContinueWork"),
        (OutcomeKind.ReportProgress, "Report.Progress"),
        (OutcomeKind.ReportDone, "Report.Done"),
        (OutcomeKind.Escalation, "Escalation"),
        (OutcomeKind.Directive, "Directive"),
        (OutcomeKind.ApprovalRequired, "ApprovalRequired"),
        (OutcomeKind.Undetermined, "Undetermined"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeKind RequireDefined(OutcomeKind value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeKind value) => Contract.ToWireValue(value);

    public static OutcomeKind ParseWireValue(string value) => Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out OutcomeKind result) =>
        Contract.TryParseWireValue(value, out result);
}

public enum OutcomeProposedIntent
{
    ContinueWork = 1,
    ReportProgress = 2,
    ReportDone = 3,
    Escalation = 4,
    Directive = 5,
    ApprovalRequired = 6,
}

public static class OutcomeProposedIntentContract
{
    private static readonly OutcomeEnumWireContract<OutcomeProposedIntent> Contract = new(
        (OutcomeProposedIntent.ContinueWork, "ContinueWork"),
        (OutcomeProposedIntent.ReportProgress, "Report.Progress"),
        (OutcomeProposedIntent.ReportDone, "Report.Done"),
        (OutcomeProposedIntent.Escalation, "Escalation"),
        (OutcomeProposedIntent.Directive, "Directive"),
        (OutcomeProposedIntent.ApprovalRequired, "ApprovalRequired"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeProposedIntent RequireDefined(
        OutcomeProposedIntent value,
        string parameterName) => Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeProposedIntent value) =>
        Contract.ToWireValue(value);

    public static OutcomeProposedIntent ParseWireValue(string value) =>
        Contract.ParseWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out OutcomeProposedIntent result) => Contract.TryParseWireValue(value, out result);
}

public enum OutcomeWorkState
{
    NotStarted = 1,
    InProgress = 2,
    Blocked = 3,
    Completed = 4,
    Failed = 5,
}

public static class OutcomeWorkStateContract
{
    private static readonly OutcomeEnumWireContract<OutcomeWorkState> Contract = new(
        (OutcomeWorkState.NotStarted, "NotStarted"),
        (OutcomeWorkState.InProgress, "InProgress"),
        (OutcomeWorkState.Blocked, "Blocked"),
        (OutcomeWorkState.Completed, "Completed"),
        (OutcomeWorkState.Failed, "Failed"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeWorkState RequireDefined(OutcomeWorkState value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeWorkState value) => Contract.ToWireValue(value);

    public static bool TryParseWireValue(string? value, out OutcomeWorkState result) =>
        Contract.TryParseWireValue(value, out result);
}

public enum OutcomeRequiredIntervention
{
    None = 1,
    HumanApproval = 2,
    SuperiorDecision = 3,
    ExternalAction = 4,
    Delegation = 5,
}

public static class OutcomeRequiredInterventionContract
{
    private static readonly OutcomeEnumWireContract<OutcomeRequiredIntervention> Contract = new(
        (OutcomeRequiredIntervention.None, "None"),
        (OutcomeRequiredIntervention.HumanApproval, "HumanApproval"),
        (OutcomeRequiredIntervention.SuperiorDecision, "SuperiorDecision"),
        (OutcomeRequiredIntervention.ExternalAction, "ExternalAction"),
        (OutcomeRequiredIntervention.Delegation, "Delegation"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeRequiredIntervention RequireDefined(
        OutcomeRequiredIntervention value,
        string parameterName) => Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeRequiredIntervention value) =>
        Contract.ToWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out OutcomeRequiredIntervention result) => Contract.TryParseWireValue(value, out result);
}

public enum OutcomeBlocker
{
    MissingInput = 1,
    HumanApproval = 2,
    SuperiorDecision = 3,
    AuthorityBoundary = 4,
    ExternalDependency = 5,
    Budget = 6,
    Deadline = 7,
    IterationLimit = 8,
    RetryLimit = 9,
    ToolFailure = 10,
    Routing = 11,
}

public static class OutcomeBlockerContract
{
    private static readonly OutcomeEnumWireContract<OutcomeBlocker> Contract = new(
        (OutcomeBlocker.MissingInput, "MissingInput"),
        (OutcomeBlocker.HumanApproval, "HumanApproval"),
        (OutcomeBlocker.SuperiorDecision, "SuperiorDecision"),
        (OutcomeBlocker.AuthorityBoundary, "AuthorityBoundary"),
        (OutcomeBlocker.ExternalDependency, "ExternalDependency"),
        (OutcomeBlocker.Budget, "Budget"),
        (OutcomeBlocker.Deadline, "Deadline"),
        (OutcomeBlocker.IterationLimit, "IterationLimit"),
        (OutcomeBlocker.RetryLimit, "RetryLimit"),
        (OutcomeBlocker.ToolFailure, "ToolFailure"),
        (OutcomeBlocker.Routing, "Routing"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeBlocker RequireDefined(OutcomeBlocker value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeBlocker value) => Contract.ToWireValue(value);

    public static bool TryParseWireValue(string? value, out OutcomeBlocker result) =>
        Contract.TryParseWireValue(value, out result);
}

public enum OutcomeEvidenceSource
{
    RuntimeFact = 1,
    DirectiveInput = 2,
    CompletionCriterion = 3,
    ToolResult = 4,
    PersistedState = 5,
}

public static class OutcomeEvidenceSourceContract
{
    private static readonly OutcomeEnumWireContract<OutcomeEvidenceSource> Contract = new(
        (OutcomeEvidenceSource.RuntimeFact, "RuntimeFact"),
        (OutcomeEvidenceSource.DirectiveInput, "DirectiveInput"),
        (OutcomeEvidenceSource.CompletionCriterion, "CompletionCriterion"),
        (OutcomeEvidenceSource.ToolResult, "ToolResult"),
        (OutcomeEvidenceSource.PersistedState, "PersistedState"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeEvidenceSource RequireDefined(
        OutcomeEvidenceSource value,
        string parameterName) => Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeEvidenceSource value) =>
        Contract.ToWireValue(value);

    public static bool TryParseWireValue(string? value, out OutcomeEvidenceSource result) =>
        Contract.TryParseWireValue(value, out result);
}

public enum OutcomeInformationGapMateriality
{
    Material = 1,
    NonMaterial = 2,
}

public static class OutcomeInformationGapMaterialityContract
{
    private static readonly OutcomeEnumWireContract<OutcomeInformationGapMateriality> Contract = new(
        (OutcomeInformationGapMateriality.Material, "Material"),
        (OutcomeInformationGapMateriality.NonMaterial, "NonMaterial"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeInformationGapMateriality RequireDefined(
        OutcomeInformationGapMateriality value,
        string parameterName) => Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeInformationGapMateriality value) =>
        Contract.ToWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out OutcomeInformationGapMateriality result) => Contract.TryParseWireValue(value, out result);
}

public enum OutcomeInformationGapMaterialityReason
{
    ChangesSeverity = 1,
    MakesNextActionUnsafe = 2,
    PreventsConclusion = 3,
}

public static class OutcomeInformationGapMaterialityReasonContract
{
    private static readonly OutcomeEnumWireContract<OutcomeInformationGapMaterialityReason> Contract = new(
        (OutcomeInformationGapMaterialityReason.ChangesSeverity, "ChangesSeverity"),
        (OutcomeInformationGapMaterialityReason.MakesNextActionUnsafe, "MakesNextActionUnsafe"),
        (OutcomeInformationGapMaterialityReason.PreventsConclusion, "PreventsConclusion"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeInformationGapMaterialityReason RequireDefined(
        OutcomeInformationGapMaterialityReason value,
        string parameterName) => Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeInformationGapMaterialityReason value) =>
        Contract.ToWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out OutcomeInformationGapMaterialityReason result) =>
        Contract.TryParseWireValue(value, out result);
}

public enum OutcomeAuthorityKind
{
    ActionDomain = 1,
    ApprovalPolicy = 2,
}

public static class OutcomeAuthorityKindContract
{
    private static readonly OutcomeEnumWireContract<OutcomeAuthorityKind> Contract = new(
        (OutcomeAuthorityKind.ActionDomain, "ActionDomain"),
        (OutcomeAuthorityKind.ApprovalPolicy, "ApprovalPolicy"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeAuthorityKind RequireDefined(
        OutcomeAuthorityKind value,
        string parameterName) => Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeAuthorityKind value) => Contract.ToWireValue(value);

    public static bool TryParseWireValue(string? value, out OutcomeAuthorityKind result) =>
        Contract.TryParseWireValue(value, out result);
}

public enum OutcomeDependencyState
{
    Available = 1,
    TransientFailure = 2,
    PermanentFailure = 3,
}

public enum OutcomeAuthorityState
{
    NotRequired = 1,
    Authorized = 2,
    Denied = 3,
    Unknown = 4,
}

public enum OutcomeRoutingState
{
    NotRequired = 1,
    Available = 2,
    Unavailable = 3,
    Unknown = 4,
}

public enum OutcomeCompletionState
{
    NotDeclared = 1,
    NotSatisfied = 2,
    Satisfied = 3,
    Unknown = 4,
    SemanticallyVerified = 5,
}

public enum OutcomePolicyTrigger
{
    SafetyRisk = 1,
    SecurityRisk = 2,
    PrivacyRisk = 3,
    ComplianceRisk = 4,
    FinancialRisk = 5,
}

public enum OutcomeResolutionReason
{
    HumanApprovalGate = 1,
    ApprovalPending = 2,
    DeadlineExceeded = 3,
    BudgetExhausted = 4,
    IterationLimitReached = 5,
    RetryLimitReached = 6,
    PermanentDependencyFailure = 7,
    AuthorityDenied = 8,
    RoutingUnavailable = 9,
    PolicyTriggerObserved = 10,
    AutonomousActionAvailable = 11,
    DelegationRequired = 12,
    CompletionCriteriaSatisfied = 13,
    VerifiableProgress = 14,
    InsufficientFacts = 15,
    ContradictoryFacts = 16,
    VerifierConfirmed = 17,
    VerifierUnavailable = 18,
    VerifierTimedOut = 19,
    VerifierOutputInvalid = 20,
    VerifierContradictedFacts = 21,
    VerifierDisagreement = 22,
    FactsUnavailable = 23,
    PolicyUnavailable = 24,
    PolicyIncompatible = 25,
    ProposalEscalation = 26,
    SemanticCompletionVerified = 27,
}

public static class OutcomeResolutionReasonContract
{
    private static readonly OutcomeEnumWireContract<OutcomeResolutionReason> Contract = new(
        (OutcomeResolutionReason.HumanApprovalGate, "human-approval-gate"),
        (OutcomeResolutionReason.ApprovalPending, "approval-pending"),
        (OutcomeResolutionReason.DeadlineExceeded, "deadline-exceeded"),
        (OutcomeResolutionReason.BudgetExhausted, "budget-exhausted"),
        (OutcomeResolutionReason.IterationLimitReached, "iteration-limit-reached"),
        (OutcomeResolutionReason.RetryLimitReached, "retry-limit-reached"),
        (OutcomeResolutionReason.PermanentDependencyFailure, "permanent-dependency-failure"),
        (OutcomeResolutionReason.AuthorityDenied, "authority-denied"),
        (OutcomeResolutionReason.RoutingUnavailable, "routing-unavailable"),
        (OutcomeResolutionReason.PolicyTriggerObserved, "policy-trigger-observed"),
        (OutcomeResolutionReason.AutonomousActionAvailable, "autonomous-action-available"),
        (OutcomeResolutionReason.DelegationRequired, "delegation-required"),
        (OutcomeResolutionReason.CompletionCriteriaSatisfied, "completion-criteria-satisfied"),
        (OutcomeResolutionReason.VerifiableProgress, "verifiable-progress"),
        (OutcomeResolutionReason.InsufficientFacts, "insufficient-facts"),
        (OutcomeResolutionReason.ContradictoryFacts, "contradictory-facts"),
        (OutcomeResolutionReason.VerifierConfirmed, "verifier-confirmed"),
        (OutcomeResolutionReason.VerifierUnavailable, "verifier-unavailable"),
        (OutcomeResolutionReason.VerifierTimedOut, "verifier-timed-out"),
        (OutcomeResolutionReason.VerifierOutputInvalid, "verifier-output-invalid"),
        (OutcomeResolutionReason.VerifierContradictedFacts, "verifier-contradicted-facts"),
        (OutcomeResolutionReason.VerifierDisagreement, "verifier-disagreement"),
        (OutcomeResolutionReason.FactsUnavailable, "facts-unavailable"),
        (OutcomeResolutionReason.PolicyUnavailable, "policy-unavailable"),
        (OutcomeResolutionReason.PolicyIncompatible, "policy-incompatible"),
        (OutcomeResolutionReason.ProposalEscalation, "proposal-escalation"),
        (OutcomeResolutionReason.SemanticCompletionVerified, "semantic-completion-verified"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeResolutionReason RequireDefined(
        OutcomeResolutionReason value,
        string parameterName) => Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeResolutionReason value) =>
        Contract.ToWireValue(value);
}

public sealed record ExecutionFacts
{
    public ExecutionFacts(
        int iterationCount,
        int retryCount,
        bool deadlineExceeded,
        bool budgetExhausted,
        bool humanApprovalRequired,
        bool approvalPending,
        OutcomeDependencyState dependencyState,
        OutcomeAuthorityState authorityState,
        OutcomeRoutingState routingState,
        bool autonomousActionAvailable,
        bool delegationRequired,
        bool pendingActions,
        bool externalInterventionRequired,
        bool verifiableProgress,
        bool responsibilityRetained,
        OutcomeCompletionState completionState,
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

        IterationCount = iterationCount;
        RetryCount = retryCount;
        DeadlineExceeded = deadlineExceeded;
        BudgetExhausted = budgetExhausted;
        HumanApprovalRequired = humanApprovalRequired;
        ApprovalPending = approvalPending;
        DependencyState = OutcomeContractGuards.RequireDefined(dependencyState, nameof(dependencyState));
        AuthorityState = OutcomeContractGuards.RequireDefined(authorityState, nameof(authorityState));
        RoutingState = OutcomeContractGuards.RequireDefined(routingState, nameof(routingState));
        AutonomousActionAvailable = autonomousActionAvailable;
        DelegationRequired = delegationRequired;
        PendingActions = pendingActions;
        ExternalInterventionRequired = externalInterventionRequired;
        VerifiableProgress = verifiableProgress;
        ResponsibilityRetained = responsibilityRetained;
        CompletionState = OutcomeContractGuards.RequireDefined(completionState, nameof(completionState));
        ObservedPolicyTriggers = OutcomeContractGuards.SnapshotDefinedDistinct(
            observedPolicyTriggers,
            nameof(observedPolicyTriggers));
    }

    public int ContractVersion => OrganizationalOutcomeContractVersions.ExecutionFacts;

    public int IterationCount { get; }

    public int RetryCount { get; }

    public bool DeadlineExceeded { get; }

    public bool BudgetExhausted { get; }

    public bool HumanApprovalRequired { get; }

    public bool ApprovalPending { get; }

    public OutcomeDependencyState DependencyState { get; }

    public OutcomeAuthorityState AuthorityState { get; }

    public OutcomeRoutingState RoutingState { get; }

    /// <summary>True only for a safe, authorized action executable by the current position.</summary>
    public bool AutonomousActionAvailable { get; }

    /// <summary>True only for an authorized downward delegation already selected by the runtime.</summary>
    public bool DelegationRequired { get; }

    public bool PendingActions { get; }

    public bool ExternalInterventionRequired { get; }

    public bool VerifiableProgress { get; }

    public bool ResponsibilityRetained { get; }

    public OutcomeCompletionState CompletionState { get; }

    public ImmutableArray<OutcomePolicyTrigger> ObservedPolicyTriggers { get; }

    public ExecutionFacts WithCompletionState(OutcomeCompletionState completionState) =>
        new(
            IterationCount,
            RetryCount,
            DeadlineExceeded,
            BudgetExhausted,
            HumanApprovalRequired,
            ApprovalPending,
            DependencyState,
            AuthorityState,
            RoutingState,
            AutonomousActionAvailable,
            DelegationRequired,
            PendingActions,
            ExternalInterventionRequired,
            VerifiableProgress,
            ResponsibilityRetained,
            completionState,
            ObservedPolicyTriggers);
}

public sealed record DirectiveExecutionRequirement
{
    public DirectiveExecutionRequirement(string reference, string description)
    {
        Reference = OutcomeContractGuards.RequireReference(reference, nameof(reference));
        Description = OutcomeContractGuards.RequireText(description, nameof(description));
    }

    public string Reference { get; }

    public string Description { get; }
}

public sealed record DirectiveExecutionContract
{
    public DirectiveExecutionContract(
        IEnumerable<DirectiveExecutionRequirement>? requiredInputs = null,
        IEnumerable<DirectiveExecutionRequirement>? completionCriteria = null)
    {
        RequiredInputs = SnapshotRequirements(requiredInputs, nameof(requiredInputs));
        CompletionCriteria = SnapshotRequirements(completionCriteria, nameof(completionCriteria));
    }

    public int ContractVersion => OrganizationalOutcomeContractVersions.DirectiveExecution;

    public ImmutableArray<DirectiveExecutionRequirement> RequiredInputs { get; }

    public ImmutableArray<DirectiveExecutionRequirement> CompletionCriteria { get; }

    private static ImmutableArray<DirectiveExecutionRequirement> SnapshotRequirements(
        IEnumerable<DirectiveExecutionRequirement>? requirements,
        string parameterName)
    {
        if (requirements is null)
        {
            return [];
        }

        var snapshot = requirements.ToImmutableArray();
        if (snapshot.Any(requirement => requirement is null))
        {
            throw new ArgumentException(
                "Directive execution requirements cannot contain null entries.",
                parameterName);
        }

        if (snapshot.Select(requirement => requirement.Reference)
            .Distinct(StringComparer.Ordinal)
            .Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Directive execution requirement references must be unique within their category.",
                parameterName);
        }

        return snapshot;
    }
}

public sealed record OutcomeEvidenceReference
{
    public OutcomeEvidenceReference(OutcomeEvidenceSource source, string reference)
    {
        Source = OutcomeEvidenceSourceContract.RequireDefined(source, nameof(source));
        Reference = OutcomeContractGuards.RequireReference(reference, nameof(reference));
    }

    public OutcomeEvidenceSource Source { get; }

    public string Reference { get; }
}

public sealed record OutcomeInformationGap
{
    public OutcomeInformationGap(
        string missingEvidenceReference,
        OutcomeInformationGapMateriality materiality,
        OutcomeInformationGapMaterialityReason? materialityReason)
    {
        MissingEvidenceReference = OutcomeContractGuards.RequireReference(
            missingEvidenceReference,
            nameof(missingEvidenceReference));
        Materiality = OutcomeInformationGapMaterialityContract.RequireDefined(
            materiality,
            nameof(materiality));
        MaterialityReason = materialityReason is { } reason
            ? OutcomeInformationGapMaterialityReasonContract.RequireDefined(
                reason,
                nameof(materialityReason))
            : null;

        if ((Materiality == OutcomeInformationGapMateriality.Material) !=
            (MaterialityReason is not null))
        {
            throw new ArgumentException(
                "Material information gaps require a closed reason and non-material gaps forbid one.",
                nameof(materialityReason));
        }
    }

    public string MissingEvidenceReference { get; }

    public OutcomeInformationGapMateriality Materiality { get; }

    public OutcomeInformationGapMaterialityReason? MaterialityReason { get; }
}

public sealed record OutcomeAuthorityRequest
{
    public OutcomeAuthorityRequest(
        string decision,
        OutcomeAuthorityKind authorityKind,
        string authorityReference,
        string positionLimitReason)
    {
        Decision = OutcomeContractGuards.RequireText(decision, nameof(decision));
        AuthorityKind = OutcomeAuthorityKindContract.RequireDefined(
            authorityKind,
            nameof(authorityKind));
        AuthorityReference = ValidateAuthorityReference(
            AuthorityKind,
            authorityReference);
        PositionLimitReason = OutcomeContractGuards.RequireText(
            positionLimitReason,
            nameof(positionLimitReason));
    }

    public string Decision { get; }

    public OutcomeAuthorityKind AuthorityKind { get; }

    public string AuthorityReference { get; }

    public string PositionLimitReason { get; }

    private static string ValidateAuthorityReference(
        OutcomeAuthorityKind kind,
        string reference)
    {
        var boundedReference = OutcomeContractGuards.RequireReference(
            reference,
            nameof(reference));
        return kind switch
        {
            OutcomeAuthorityKind.ActionDomain => AuthorityKey.From(boundedReference).Value,
            OutcomeAuthorityKind.ApprovalPolicy => ApprovalPolicyRef.From(boundedReference).Value,
            _ => throw new InvalidOperationException("Validated authority kind is not mapped."),
        };
    }
}

public sealed record OutcomeProposal
{
    public OutcomeProposal(
        OutcomeProposedIntent proposedIntent,
        OutcomeWorkState workState,
        OutcomeRequiredIntervention requiredIntervention,
        IEnumerable<OutcomeBlocker>? blockers,
        string? nextAction,
        IEnumerable<OutcomeEvidenceReference>? evidenceReferences,
        IEnumerable<OutcomeInformationGap>? informationGaps = null,
        OutcomeAuthorityRequest? authorityRequest = null)
    {
        ProposedIntent = OutcomeProposedIntentContract.RequireDefined(
            proposedIntent,
            nameof(proposedIntent));
        WorkState = OutcomeWorkStateContract.RequireDefined(workState, nameof(workState));
        RequiredIntervention = OutcomeRequiredInterventionContract.RequireDefined(
            requiredIntervention,
            nameof(requiredIntervention));
        Blockers = OutcomeContractGuards.SnapshotDefinedDistinct(blockers, nameof(blockers));
        NextAction = OutcomeContractGuards.OptionalText(nextAction, nameof(nextAction));
        EvidenceReferences = SnapshotEvidence(evidenceReferences);
        InformationGaps = SnapshotInformationGaps(informationGaps);
        AuthorityRequest = authorityRequest;

        OutcomeProposalRules.RequireValidCombination(
            ProposedIntent,
            WorkState,
            RequiredIntervention,
            Blockers,
            NextAction,
            EvidenceReferences);
    }

    public int ContractVersion => OrganizationalOutcomeContractVersions.OutcomeProposal;

    public OutcomeProposedIntent ProposedIntent { get; }

    public OutcomeWorkState WorkState { get; }

    public OutcomeRequiredIntervention RequiredIntervention { get; }

    public ImmutableArray<OutcomeBlocker> Blockers { get; }

    public string? NextAction { get; }

    public ImmutableArray<OutcomeEvidenceReference> EvidenceReferences { get; }

    public ImmutableArray<OutcomeInformationGap> InformationGaps { get; }

    public OutcomeAuthorityRequest? AuthorityRequest { get; }

    private static ImmutableArray<OutcomeEvidenceReference> SnapshotEvidence(
        IEnumerable<OutcomeEvidenceReference>? evidenceReferences)
    {
        if (evidenceReferences is null)
        {
            return [];
        }

        var snapshot = evidenceReferences.ToImmutableArray();
        if (snapshot.Any(reference => reference is null))
        {
            throw new ArgumentException(
                "Outcome evidence references cannot contain null entries.",
                nameof(evidenceReferences));
        }

        if (snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Outcome evidence references must be unique.",
                nameof(evidenceReferences));
        }

        return snapshot;
    }

    private static ImmutableArray<OutcomeInformationGap> SnapshotInformationGaps(
        IEnumerable<OutcomeInformationGap>? informationGaps)
    {
        if (informationGaps is null)
        {
            return [];
        }

        var snapshot = informationGaps.ToImmutableArray();
        if (snapshot.Any(gap => gap is null))
        {
            throw new ArgumentException(
                "Outcome information gaps cannot contain null entries.",
                nameof(informationGaps));
        }

        if (snapshot.Select(gap => gap.MissingEvidenceReference)
            .Distinct(StringComparer.Ordinal)
            .Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Outcome information gaps must use unique missing evidence references.",
                nameof(informationGaps));
        }

        return snapshot;
    }
}

public sealed record OutcomePolicySnapshot
{
    public OutcomePolicySnapshot(
        string version,
        string fingerprint,
        int maximumIterations,
        int maximumRetries,
        bool verifierEnabled,
        IEnumerable<OutcomePolicyTrigger>? escalationTriggers = null)
    {
        if (maximumIterations < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumIterations),
                maximumIterations,
                "Maximum iterations cannot be negative.");
        }

        if (maximumRetries < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRetries),
                maximumRetries,
                "Maximum retries cannot be negative.");
        }

        Version = OutcomeContractGuards.RequireText(version, nameof(version));
        Fingerprint = OutcomeContractGuards.RequireReference(fingerprint, nameof(fingerprint));
        MaximumIterations = maximumIterations;
        MaximumRetries = maximumRetries;
        VerifierEnabled = verifierEnabled;
        EscalationTriggers = OutcomeContractGuards.SnapshotDefinedDistinct(
            escalationTriggers,
            nameof(escalationTriggers));
    }

    public int ContractVersion => OrganizationalOutcomeContractVersions.PolicySnapshot;

    public string Version { get; }

    public string Fingerprint { get; }

    public int MaximumIterations { get; }

    public int MaximumRetries { get; }

    public bool VerifierEnabled { get; }

    public ImmutableArray<OutcomePolicyTrigger> EscalationTriggers { get; }

    public OutcomeKind FailSafeOutcome => OutcomeKind.Escalation;
}

public sealed record OutcomeResolution
{
    public OutcomeResolution(
        OutcomeKind outcome,
        IEnumerable<OutcomeResolutionReason> reasons,
        string policyVersion,
        string policyFingerprint,
        bool proposalOverridden,
        bool verifierInvoked,
        OutcomeVerifierResultStatus? verifierStatus = null,
        OutcomeVerifierClassification? verifierClassification = null,
        bool semanticCompletionCandidate = false,
        IEnumerable<OutcomeSemanticCompletionIneligibilityReason>?
            semanticCompletionIneligibilityReasons = null)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        var reasonSnapshot = reasons.ToImmutableArray();
        if (reasonSnapshot.IsEmpty)
        {
            throw new ArgumentException(
                "An outcome resolution must contain at least one reason.",
                nameof(reasons));
        }

        if (reasonSnapshot.Distinct().Count() != reasonSnapshot.Length)
        {
            throw new ArgumentException(
                "Outcome resolution reasons must be unique.",
                nameof(reasons));
        }

        Outcome = OutcomeKindContract.RequireDefined(outcome, nameof(outcome));
        Reasons = reasonSnapshot
            .Select(reason => OutcomeResolutionReasonContract.RequireDefined(reason, nameof(reasons)))
            .ToImmutableArray();
        PolicyVersion = OutcomeContractGuards.RequireText(policyVersion, nameof(policyVersion));
        PolicyFingerprint = OutcomeContractGuards.RequireReference(
            policyFingerprint,
            nameof(policyFingerprint));
        ProposalOverridden = proposalOverridden;
        VerifierInvoked = verifierInvoked;
        if (verifierStatus is null && verifierClassification is not null)
        {
            throw new ArgumentException(
                "A verifier classification requires a verifier result status.",
                nameof(verifierClassification));
        }

        if (verifierStatus is { } status)
        {
            VerifierStatus = OutcomeVerifierResultStatusContract.RequireDefined(
                status,
                nameof(verifierStatus));
            if ((status == OutcomeVerifierResultStatus.Classified) !=
                verifierClassification.HasValue)
            {
                throw new ArgumentException(
                    "Only a classified verifier result can carry a classification.",
                    nameof(verifierClassification));
            }
        }

        VerifierClassification = verifierClassification is null
            ? null
            : OutcomeVerifierClassificationContract.RequireDefined(
                verifierClassification.Value,
                nameof(verifierClassification));
        SemanticCompletionIneligibilityReasons =
            semanticCompletionIneligibilityReasons is null
                ? null
                : semanticCompletionIneligibilityReasons
                    .Select(reason =>
                        OutcomeSemanticCompletionIneligibilityReasonContract.RequireDefined(
                            reason,
                            nameof(semanticCompletionIneligibilityReasons)))
                    .Distinct()
                    .Order()
                    .ToImmutableArray();
        if (semanticCompletionCandidate &&
            SemanticCompletionIneligibilityReasons is not { IsEmpty: true })
        {
            throw new ArgumentException(
                "An eligible semantic-completion candidate must have an evaluated empty reason set.",
                nameof(semanticCompletionIneligibilityReasons));
        }

        if (!semanticCompletionCandidate &&
            SemanticCompletionIneligibilityReasons is { IsEmpty: true })
        {
            throw new ArgumentException(
                "An evaluated ineligible semantic-completion candidate must have at least one reason.",
                nameof(semanticCompletionIneligibilityReasons));
        }

        SemanticCompletionCandidate = semanticCompletionCandidate;
    }

    public int ContractVersion => OrganizationalOutcomeContractVersions.OutcomeResolution;

    public OutcomeKind Outcome { get; }

    public ImmutableArray<OutcomeResolutionReason> Reasons { get; }

    public string PolicyVersion { get; }

    public string PolicyFingerprint { get; }

    public bool ProposalOverridden { get; }

    public bool VerifierInvoked { get; }

    public OutcomeVerifierResultStatus? VerifierStatus { get; }

    public OutcomeVerifierClassification? VerifierClassification { get; }

    public bool SemanticCompletionCandidate { get; }

    public ImmutableArray<OutcomeSemanticCompletionIneligibilityReason>?
        SemanticCompletionIneligibilityReasons { get; }
}

internal static class OutcomeProposalRules
{
    public static void RequireValidCombination(
        OutcomeProposedIntent proposedIntent,
        OutcomeWorkState workState,
        OutcomeRequiredIntervention requiredIntervention,
        IReadOnlyCollection<OutcomeBlocker> blockers,
        string? nextAction,
        IReadOnlyCollection<OutcomeEvidenceReference> evidenceReferences)
    {
        var valid = proposedIntent switch
        {
            OutcomeProposedIntent.ContinueWork =>
                workState is OutcomeWorkState.NotStarted or OutcomeWorkState.InProgress &&
                requiredIntervention is OutcomeRequiredIntervention.None &&
                blockers.Count == 0 &&
                nextAction is not null,
            OutcomeProposedIntent.ReportProgress =>
                workState is OutcomeWorkState.InProgress &&
                requiredIntervention is OutcomeRequiredIntervention.None &&
                blockers.Count == 0 &&
                nextAction is not null &&
                evidenceReferences.Count > 0,
            OutcomeProposedIntent.ReportDone =>
                workState is OutcomeWorkState.Completed &&
                requiredIntervention is OutcomeRequiredIntervention.None &&
                blockers.Count == 0 &&
                nextAction is null &&
                evidenceReferences.Count > 0,
            OutcomeProposedIntent.Escalation =>
                workState is OutcomeWorkState.Blocked or OutcomeWorkState.Failed &&
                requiredIntervention is OutcomeRequiredIntervention.SuperiorDecision or
                    OutcomeRequiredIntervention.ExternalAction &&
                blockers.Count > 0 &&
                !blockers.Contains(OutcomeBlocker.HumanApproval),
            OutcomeProposedIntent.Directive =>
                workState is OutcomeWorkState.NotStarted or OutcomeWorkState.InProgress &&
                requiredIntervention is OutcomeRequiredIntervention.Delegation &&
                blockers.Count == 0 &&
                nextAction is not null,
            OutcomeProposedIntent.ApprovalRequired =>
                workState is OutcomeWorkState.Blocked &&
                requiredIntervention is OutcomeRequiredIntervention.HumanApproval &&
                blockers.Count == 1 &&
                blockers.Contains(OutcomeBlocker.HumanApproval),
            _ => false,
        };

        if (!valid)
        {
            throw new ArgumentException(
                "Outcome proposal fields form a contradictory combination.",
                nameof(proposedIntent));
        }
    }
}

internal static class OutcomeContractGuards
{
    private const int MaximumReferenceLength = 128;

    public static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        return normalized;
    }

    public static string? OptionalText(string? value, string parameterName) =>
        value is null ? null : RequireText(value, parameterName);

    public static string RequireReference(string value, string parameterName)
    {
        var reference = RequireText(value, parameterName);
        if (reference.Length > MaximumReferenceLength ||
            !reference.All(IsReferenceCharacter))
        {
            throw new ArgumentException(
                "Reference must be an opaque identifier containing only letters, digits, '.', '_', ':', '/', or '-'.",
                parameterName);
        }

        return reference;
    }

    public static TEnum RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"{typeof(TEnum).Name} has an undefined value.");
        }

        return value;
    }

    public static ImmutableArray<TEnum> SnapshotDefinedDistinct<TEnum>(
        IEnumerable<TEnum>? values,
        string parameterName)
        where TEnum : struct, Enum
    {
        if (values is null)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<TEnum>();
        foreach (var value in values)
        {
            var defined = RequireDefined(value, parameterName);
            if (builder.Contains(defined))
            {
                throw new ArgumentException(
                    $"{typeof(TEnum).Name} values must be unique.",
                    parameterName);
            }

            builder.Add(defined);
        }

        return builder.ToImmutable();
    }

    private static bool IsReferenceCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or ':' or '/' or '-';
}

internal sealed class OutcomeEnumWireContract<TEnum>
    where TEnum : struct, Enum
{
    private readonly IReadOnlyDictionary<TEnum, string> _wireByValue;
    private readonly IReadOnlyDictionary<string, TEnum> _valueByWire;

    public OutcomeEnumWireContract(params (TEnum Value, string WireValue)[] entries)
    {
        _wireByValue = entries.ToDictionary(entry => entry.Value, entry => entry.WireValue);
        _valueByWire = entries.ToDictionary(
            entry => entry.WireValue,
            entry => entry.Value,
            StringComparer.Ordinal);
        WireValues = entries.Select(entry => entry.WireValue).ToImmutableArray();
    }

    public ImmutableArray<string> WireValues { get; }

    public TEnum RequireDefined(TEnum value, string parameterName)
    {
        if (!_wireByValue.ContainsKey(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"{typeof(TEnum).Name} has an undefined value.");
        }

        return value;
    }

    public string ToWireValue(TEnum value)
    {
        RequireDefined(value, nameof(value));
        return _wireByValue[value];
    }

    public TEnum ParseWireValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_valueByWire.TryGetValue(value, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"{typeof(TEnum).Name} has an invalid wire value.",
            nameof(value));
    }

    public bool TryParseWireValue(string? value, out TEnum result)
    {
        if (value is not null && _valueByWire.TryGetValue(value, out result))
        {
            return true;
        }

        result = default;
        return false;
    }
}
