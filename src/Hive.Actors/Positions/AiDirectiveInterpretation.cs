using System.Collections.Immutable;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Domain.Outcomes;

namespace Hive.Actors.Positions;

internal enum AiDirectiveInterpretationOutcomeKind
{
    DecisionAccepted = 1,
    StructuredError = 2,
    EscalationRequired = 3,
}

internal sealed record AiDirectiveInterpretationFailure
{
    public AiDirectiveInterpretationFailure(
        string code,
        string auditReason,
        bool isRetryable,
        IEnumerable<AiDirectiveDecisionParseError>? parseErrors = null,
        AiGatewayError? gatewayError = null,
        AiDirectiveDecision? acceptedDecision = null,
        OutcomeProposal? acceptedProposal = null)
    {
        Code = AiAgentGatewayText.Require(code, nameof(code));
        AuditReason = AiAgentGatewayText.Require(auditReason, nameof(auditReason));
        IsRetryable = isRetryable;
        ParseErrors = SnapshotParseErrors(parseErrors);
        GatewayError = gatewayError;
        AcceptedDecision = acceptedDecision;
        AcceptedProposal = acceptedProposal;
    }

    public string Code { get; }

    public string AuditReason { get; }

    public bool IsRetryable { get; }

    public ImmutableArray<AiDirectiveDecisionParseError> ParseErrors { get; }

    public AiGatewayError? GatewayError { get; }

    public AiDirectiveDecision? AcceptedDecision { get; }

    public OutcomeProposal? AcceptedProposal { get; }

    private static ImmutableArray<AiDirectiveDecisionParseError> SnapshotParseErrors(
        IEnumerable<AiDirectiveDecisionParseError>? parseErrors)
    {
        if (parseErrors is null)
        {
            return ImmutableArray<AiDirectiveDecisionParseError>.Empty;
        }

        var snapshot = parseErrors.ToImmutableArray();
        if (snapshot.Any(error => error is null))
        {
            throw new ArgumentException(
                "AI directive interpretation parse errors cannot contain null entries.",
                nameof(parseErrors));
        }

        return snapshot;
    }
}

internal sealed record AiDirectiveInterpretationResult
{
    private AiDirectiveInterpretationResult(
        string correlationId,
        AiDirectiveInterpretationOutcomeKind outcome,
        AiDirectiveDecision? decision,
        OutcomeProposal? proposal,
        AiDirectiveInterpretationFailure? failure)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Outcome = outcome;
        Decision = decision;
        Proposal = proposal;
        Failure = failure;
    }

    public string CorrelationId { get; }

    public AiDirectiveInterpretationOutcomeKind Outcome { get; }

    public AiDirectiveDecision? Decision { get; }

    public OutcomeProposal? Proposal { get; }

    public AiDirectiveInterpretationFailure? Failure { get; }

    public bool IsDecision => Outcome == AiDirectiveInterpretationOutcomeKind.DecisionAccepted;

    public bool IsFailure => !IsDecision;

    public bool RequiresEscalation => Outcome == AiDirectiveInterpretationOutcomeKind.EscalationRequired;

    public bool IsStructuredError => Outcome == AiDirectiveInterpretationOutcomeKind.StructuredError;

    public static AiDirectiveInterpretationResult AcceptedDecision(
        string correlationId,
        AiDirectiveDecision decision,
        OutcomeProposal? proposal = null)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new AiDirectiveInterpretationResult(
            correlationId,
            AiDirectiveInterpretationOutcomeKind.DecisionAccepted,
            decision,
            proposal,
            failure: null);
    }

    public static AiDirectiveInterpretationResult StructuredError(
        string correlationId,
        AiDirectiveInterpretationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new AiDirectiveInterpretationResult(
            correlationId,
            AiDirectiveInterpretationOutcomeKind.StructuredError,
            decision: null,
            proposal: null,
            failure);
    }

    public static AiDirectiveInterpretationResult EscalationRequired(
        string correlationId,
        AiDirectiveInterpretationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new AiDirectiveInterpretationResult(
            correlationId,
            AiDirectiveInterpretationOutcomeKind.EscalationRequired,
            decision: null,
            proposal: null,
            failure);
    }
}

internal static class AiDirectiveDecisionInterpreter
{
    public static AiDirectiveInterpretationResult Interpret(
        AiAgentGatewayInvocationResult invocation,
        IEnumerable<AuthorityKey>? canDecide = null,
        bool requireOutcomeProposal = false,
        OutcomeProposalEvidenceContext? outcomeProposalEvidenceContext = null,
        bool allowProgressReports = false)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (invocation.IsFailure)
        {
            var failure = GatewayFailure(invocation.FailureReason);
            return AiDirectiveInterpretationResult.StructuredError(
                invocation.CorrelationId,
                failure);
        }

        var parseResult = AiDirectiveDecisionParser.Parse(
            invocation.Response.Text,
            canDecide,
            requireOutcomeProposal,
            outcomeProposalEvidenceContext,
            allowProgressReports);
        if (parseResult.IsSuccess)
        {
            if (!allowProgressReports &&
                (parseResult.Decision is
                    AiDirectiveReportDecision { Kind: Hive.Domain.Messaging.ReportKind.Progress } ||
                 parseResult.Proposal?.ProposedIntent == OutcomeProposedIntent.ReportProgress))
            {
                return AiDirectiveInterpretationResult.EscalationRequired(
                    invocation.CorrelationId,
                    new AiDirectiveInterpretationFailure(
                        "directive-execution-policy-violation",
                        "directive-execution-policy-violation: a single-shot directive cannot return a progress report.",
                        isRetryable: false,
                        [new AiDirectiveDecisionParseError(
                            AiDirectiveDecisionParseDiagnosticContract.InvalidVocabularyCode,
                            $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.ReportPayloadProperty}.{AiDirectiveDecisionSchema.ReportKindField}")]));
            }

            return AiDirectiveInterpretationResult.AcceptedDecision(
                invocation.CorrelationId,
                parseResult.Decision!,
                parseResult.Proposal);
        }

        return AiDirectiveInterpretationResult.EscalationRequired(
            invocation.CorrelationId,
            InvalidOutputFailure(parseResult));
    }

    private static AiDirectiveInterpretationFailure GatewayFailure(AiGatewayError? error)
    {
        if (error is null)
        {
            return new AiDirectiveInterpretationFailure(
                "ai-gateway-failure",
                "ai-gateway-failure: AI gateway request failed without a structured reason.",
                isRetryable: false);
        }

        var code = AiGatewayErrorCodeContract.ToWireValue(error.Code);
        return new AiDirectiveInterpretationFailure(
            "ai-gateway-failure",
            $"ai-gateway-failure: AI gateway request failed with '{code}'.",
            error.IsRetryable,
            gatewayError: error);
    }

    private static AiDirectiveInterpretationFailure InvalidOutputFailure(
        AiDirectiveDecisionParseResult parseResult)
    {
        var parseErrors = parseResult.Errors;
        var details = string.Join(
            ", ",
            parseErrors.Select(error => $"{error.Path}:{error.Code}"));

        return new AiDirectiveInterpretationFailure(
            "ai-output-invalid",
            $"ai-output-invalid: AI directive output failed strict interpretation ({details}).",
            isRetryable: false,
            parseErrors,
            acceptedDecision: parseResult.AcceptedDecision,
            acceptedProposal: parseResult.AcceptedProposal);
    }
}

internal sealed record GetAiDirectiveInterpretationResult
{
    public GetAiDirectiveInterpretationResult(string correlationId)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

internal sealed record AiDirectiveInterpretationQueryResult
{
    private AiDirectiveInterpretationQueryResult(
        string correlationId,
        AiDirectiveInterpretationResult? result)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Result = result;
    }

    public string CorrelationId { get; }

    public AiDirectiveInterpretationResult? Result { get; }

    public bool Found => Result is not null;

    public static AiDirectiveInterpretationQueryResult FoundResult(
        AiDirectiveInterpretationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AiDirectiveInterpretationQueryResult(
            result.CorrelationId,
            result);
    }

    public static AiDirectiveInterpretationQueryResult Missing(string correlationId) =>
        new(correlationId, result: null);
}
