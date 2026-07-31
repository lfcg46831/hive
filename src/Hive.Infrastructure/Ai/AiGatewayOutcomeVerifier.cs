using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Outcomes;
using Hive.Domain.Positions;

namespace Hive.Infrastructure.Ai;

/// <summary>
/// Limited semantic verifier adapter over the existing AI gateway. It sends only the bounded
/// verification contract and deliberately supplies neither tools nor conversation history.
/// </summary>
public sealed class AiGatewayOutcomeVerifier : IOutcomeVerifier
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IAiGateway _gateway;
    private readonly IPositionConfigurationProvider? _positionConfigurations;

    public AiGatewayOutcomeVerifier(
        IAiGateway gateway,
        IPositionConfigurationProvider positionConfigurations)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _positionConfigurations = positionConfigurations ??
            throw new ArgumentNullException(nameof(positionConfigurations));
    }

    internal AiGatewayOutcomeVerifier(IAiGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public async Task<OutcomeVerifierResult> VerifyAsync(
        OutcomeVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Artifact is null)
        {
            return OutcomeVerifierResult.Unavailable();
        }

        var gatewayConfiguration = await LoadGatewayConfigurationAsync(
            request.Context,
            cancellationToken).ConfigureAwait(false);
        if (_positionConfigurations is not null && gatewayConfiguration is null)
        {
            return OutcomeVerifierResult.Unavailable();
        }

        var gatewayRequest = CreateGatewayRequest(request, gatewayConfiguration);
        var response = await _gateway
            .CompleteAsync(gatewayRequest, cancellationToken)
            .ConfigureAwait(false);
        if (response is null)
        {
            return OutcomeVerifierResult.Unavailable();
        }

        if (response.IsFailure)
        {
            return response.Error!.Code == AiGatewayErrorCode.Timeout
                ? OutcomeVerifierResult.TimedOut()
                : OutcomeVerifierResult.Unavailable();
        }

        if (!HasMatchingCorrelation(gatewayRequest, response) ||
            response.ToolCalls.Count > 0 ||
            response.Text is null)
        {
            return OutcomeVerifierResult.InvalidOutput();
        }

        var parsed = OutcomeVerifierParser.Parse(response.Text);
        return parsed.IsSuccess
            ? OutcomeVerifierResult.Classified(parsed.Classification!.Value)
            : OutcomeVerifierResult.InvalidOutput();
    }

    private async Task<AiPositionRuntimeConfiguration?> LoadGatewayConfigurationAsync(
        OutcomeVerificationContext context,
        CancellationToken cancellationToken)
    {
        if (_positionConfigurations is null)
        {
            return null;
        }

        var entityId = PositionEntityId.From(context.OrganizationId, context.PositionId);
        var result = await _positionConfigurations
            .LoadAsync(entityId, cancellationToken)
            .ConfigureAwait(false);
        return result.Status == PositionRuntimeConfigurationLoadStatus.Loaded &&
            result.Configuration is { } configuration &&
            configuration.Matches(entityId)
                ? configuration.Occupant.AiGateway
                : null;
    }

    private static AiGatewayRequest CreateGatewayRequest(
        OutcomeVerificationRequest request,
        AiPositionRuntimeConfiguration? gatewayConfiguration)
    {
        // Reasoning-model output budgets include internal reasoning tokens. A 64-token cap can
        // therefore finish before the closed JSON classification is emitted even though its
        // visible payload is tiny. Keep a bounded verifier-specific ceiling while allowing the
        // configured model enough room to produce the constrained answer.
        const int verifierMaxOutputTokens = 2048;
        var maxOutputTokens = Math.Min(
            verifierMaxOutputTokens,
            gatewayConfiguration?.Parameters.MaxOutputTokens ?? verifierMaxOutputTokens);
        var timeout = gatewayConfiguration?.PerCallTimeout is { } configuredTimeout &&
            configuredTimeout < request.Context.Timeout
                ? configuredTimeout
                : request.Context.Timeout;
        var policy = gatewayConfiguration is null
            ? null
            : new AiGatewayPolicy(
                [gatewayConfiguration.Primary],
                hasAvailableBudget: !request.Facts.BudgetExhausted,
                maxOutputTokens,
                maxTimeout: timeout,
                allowedProcessingModes: gatewayConfiguration.ProcessingMode is { } mode
                    ? [mode]
                    : null,
                authorizedTools: []);

        return new AiGatewayRequest(
            request.Context.OrganizationId,
            request.Context.PositionId,
            request.Context.ThreadId,
            request.Context.MessageId,
            JsonSerializer.Serialize(CreatePayload(request), SerializerOptions),
            systemInstruction: OutcomeVerifierConstraint.SystemInstruction,
            contextMessages: [],
            tools: [],
            // GPT-5-family providers reject explicit non-default temperature values. The
            // verifier is constrained by its closed schema and prompt; leave sampling at the
            // provider/model default instead of making the call configuration-invalid.
            modelParameters: new AiModelParameters(temperature: null, maxOutputTokens),
            metadata: Metadata(request),
            provider: gatewayConfiguration?.Primary,
            processingMode: gatewayConfiguration?.ProcessingMode,
            timeout,
            policy,
            outputConstraint: OutcomeVerifierConstraint.OutputConstraint);
    }

    private static IReadOnlyDictionary<string, string> Metadata(
        OutcomeVerificationRequest request)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["directive_id"] = request.Context.DirectiveId.ToString(),
            ["iteration"] = request.Facts.IterationCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["hive.operation"] = "outcome-verification",
            ["hive.contract-version"] = request.ContractVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["hive.policy-fingerprint"] = request.Policy.Fingerprint,
        };
        if (request.Context.ExecutionLimitsVersion is { } version)
        {
            metadata["hive.execution-limits-version"] = version.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        AddTimeoutMetadata(
            metadata,
            "hive.execution-budget-ms",
            request.Context.ExecutionBudget);
        AddTimeoutMetadata(
            metadata,
            "hive.per-call-timeout-ms",
            request.Context.PerCallTimeout);
        return metadata;
    }

    private static void AddTimeoutMetadata(
        IDictionary<string, string> metadata,
        string key,
        TimeSpan? timeout)
    {
        if (timeout is { } value)
        {
            metadata[key] = value.TotalMilliseconds.ToString(
                "R",
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static object CreatePayload(OutcomeVerificationRequest request) => new
    {
        context = request.Context.Entries.Select(entry => new
        {
            entry.Reference,
            entry.Value,
        }),
        proposed_artifact = new
        {
            kind = OutcomeKindContract.ToWireValue(request.Artifact!.Kind),
            fields = request.Artifact.Entries.Select(entry => new
            {
                entry.Reference,
                entry.Value,
            }),
        },
        facts = new
        {
            request.Facts.IterationCount,
            request.Facts.RetryCount,
            request.Facts.DeadlineExceeded,
            request.Facts.BudgetExhausted,
            request.Facts.HumanApprovalRequired,
            request.Facts.ApprovalPending,
            dependency_state = request.Facts.DependencyState.ToString(),
            authority_state = request.Facts.AuthorityState.ToString(),
            routing_state = request.Facts.RoutingState.ToString(),
            request.Facts.AutonomousActionAvailable,
            request.Facts.DelegationRequired,
            request.Facts.PendingActions,
            request.Facts.ExternalInterventionRequired,
            request.Facts.VerifiableProgress,
            request.Facts.ResponsibilityRetained,
            completion_state = request.Facts.CompletionState.ToString(),
            observed_policy_triggers = request.Facts.ObservedPolicyTriggers
                .Select(trigger => trigger.ToString()),
        },
        directive = new
        {
            required_inputs = request.Directive.RequiredInputs.Select(
                requirement => requirement.Reference),
            completion_criteria = request.Directive.CompletionCriteria.Select(
                requirement => requirement.Reference),
        },
        proposal = new
        {
            proposed_intent = OutcomeProposedIntentContract.ToWireValue(
                request.Proposal.ProposedIntent),
            work_state = OutcomeWorkStateContract.ToWireValue(request.Proposal.WorkState),
            required_intervention = OutcomeRequiredInterventionContract.ToWireValue(
                request.Proposal.RequiredIntervention),
            blockers = request.Proposal.Blockers.Select(OutcomeBlockerContract.ToWireValue),
            next_action_present = request.Proposal.NextAction is not null,
            semantic_completion_candidate =
                OutcomeSemanticCompletionEligibility.IsEligible(request),
            evidence_references = request.Proposal.EvidenceReferences.Select(reference => new
            {
                source = OutcomeEvidenceSourceContract.ToWireValue(reference.Source),
                reference.Reference,
            }),
        },
        policy = new
        {
            request.Policy.Version,
            request.Policy.Fingerprint,
            request.Policy.MaximumIterations,
            request.Policy.MaximumRetries,
            request.Policy.VerifierEnabled,
            escalation_triggers = request.Policy.EscalationTriggers
                .Select(trigger => trigger.ToString()),
        },
    };

    private static bool HasMatchingCorrelation(
        AiGatewayRequest request,
        AiGatewayResponse response) =>
        request.OrganizationId == response.OrganizationId &&
        request.PositionId == response.PositionId &&
        request.ThreadId == response.ThreadId &&
        request.MessageId == response.MessageId;
}
