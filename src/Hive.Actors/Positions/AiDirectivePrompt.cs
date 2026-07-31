using System.Linq;
using System.Text;
using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Outcomes;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

internal static class AiDirectivePrompt
{
    public static AiGatewayRequest CreateInitialRequest(AiDirectiveExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var systemInstruction = BuildSystemInstructionSections(context);
        var selectedContext = AiDirectiveContextSelector.Select(context);

        return new AiGatewayRequest(
            context.OrganizationId,
            context.PositionId,
            context.Directive.ThreadId,
            context.Directive.MessageId,
            BuildContent(context, selectedContext),
            systemInstruction.Compose(),
            tools: GatewayTools(context),
            modelParameters: EffectiveModelParameters(context),
            metadata: Metadata(context),
            provider: context.Provider,
            processingMode: context.ProcessingMode,
            timeout: context.Limits.PerCallTimeout,
            policy: Policy(context),
            outputConstraint: OutputConstraint(context));
    }

    public static AiGatewayRequest CreateOutcomeProposalCorrectionRequest(
        AiDirectiveExecutionContext context,
        IEnumerable<AiDirectiveDecisionParseError> parseErrors)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parseErrors);
        if (!context.RequiresStructuredOutcomeProposal)
        {
            throw new ArgumentException(
                "Outcome proposal correction requires structured outcome resolution.",
                nameof(context));
        }

        var initial = CreateInitialRequest(context);
        var evidenceContext =
            AiDirectiveOutcomeEvidenceContext.CreateProposalContext(context);
        var correction = AiDirectiveOutcomeProposalCorrection.CreateBoundedInstruction(
            evidenceContext,
            parseErrors);
        var metadata = new Dictionary<string, string>(
            initial.Metadata,
            StringComparer.Ordinal)
        {
            ["hive.correction"] = "outcome-proposal-evidence",
        };

        return new AiGatewayRequest(
            initial.OrganizationId,
            initial.PositionId,
            initial.ThreadId,
            initial.MessageId,
            $"{initial.Content}{Environment.NewLine}{Environment.NewLine}{correction}",
            initial.SystemInstruction,
            initial.ContextMessages,
            initial.Tools,
            initial.ModelParameters,
            metadata,
            initial.Provider,
            initial.ProcessingMode,
            initial.Timeout,
            initial.Policy,
            initial.OutputConstraint);
    }

    internal static AiDirectiveSystemInstructionSections BuildSystemInstructionSections(
        AiDirectiveExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var identityPrompt = RequireIdentityPrompt(context);
        var evidenceContext = context.RequiresStructuredOutcomeProposal
            ? AiDirectiveOutcomeEvidenceContext.CreateProposalContext(context)
            : null;

        return new AiDirectiveSystemInstructionSections(
            identityPrompt.Content.Trim(),
            BuildHiveProtocolInstruction(evidenceContext),
            BuildRuntimeAuthorityInstruction(context),
            BuildRuntimeToolsInstruction(context));
    }

    private static string BuildHiveProtocolInstruction(
        OutcomeProposalEvidenceContext? evidenceContext)
    {
        var reportIntent = AiDirectiveDecisionIntentContract.ToWireValue(
            AiDirectiveDecisionIntent.Report);
        var escalationIntent = AiDirectiveDecisionIntentContract.ToWireValue(
            AiDirectiveDecisionIntent.Escalation);
        var directiveIntent = AiDirectiveDecisionIntentContract.ToWireValue(
            AiDirectiveDecisionIntent.Directive);

        var lines = new List<string>
        {
            "You are the HIVE AI occupant for the current position.",
            "Classify the directive using only the provided context.",
            "Return JSON only with no Markdown fences or explanatory prose.",
            $"Set \"{AiDirectiveDecisionSchema.SchemaVersionProperty}\" to {AiDirectiveDecisionSchema.SchemaVersion}.",
            $"Include required top-level \"{AiToolActingUnderSchema.PropertyName}\" and exactly one \"{AiDirectiveDecisionSchema.DecisionProperty}\" object for every organizational message output.",
            $"Inside \"{AiDirectiveDecisionSchema.DecisionProperty}\", use exactly one \"{AiDirectiveDecisionSchema.IntentProperty}\" value and its single matching payload: \"{reportIntent}\", \"{escalationIntent}\", or \"{directiveIntent}\".",
            $"Choose {reportIntent} only for information or an assessment that asks the superior for no decision, authorization, or choice.",
            $"Choose {escalationIntent} whenever the response asks the superior to decide, authorize, or choose; never place such a request inside {reportIntent}.",
            $"A recommendation about a future action is informational and does not by itself request or exercise authorization. Normal downstream implementation, deployment, prioritization, or change control does not alone make it an {escalationIntent}; choose {escalationIntent} only when the response asks the superior to decide, authorize, or choose now.",
            "Apply these intent rules without exposing intermediate reasoning; return only the required structured fields.",
            $"For {reportIntent}, include {AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.ReportPayloadProperty}.{AiDirectiveDecisionSchema.ReportKindField} as \"Progress\" or \"Done\" and {AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.ReportPayloadProperty}.{AiDirectiveDecisionSchema.ReportBodyField}.",
            $"For {escalationIntent}, include {AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.EscalationPayloadProperty}.{AiDirectiveDecisionSchema.EscalationIssueField}, {AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.EscalationPayloadProperty}.{AiDirectiveDecisionSchema.EscalationContextField}, and {AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.EscalationPayloadProperty}.{AiDirectiveDecisionSchema.EscalationOptionsConsideredField}.",
            $"For {directiveIntent}, include {AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.DirectivePayloadProperty}.{AiDirectiveDecisionSchema.DirectiveTargetPositionIdField}, {AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.DirectivePayloadProperty}.{AiDirectiveDecisionSchema.DirectiveObjectiveField}, and {AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.DirectivePayloadProperty}.{AiDirectiveDecisionSchema.DirectiveContextField}.",
        };
        if (evidenceContext is not null)
        {
            lines.AddRange(
            [
                $"Include the required top-level \"{AiDirectiveOutcomeProposalEnvelope.PropertyName}\" object using OutcomeProposal schema version {OutcomeProposalConstraint.SchemaVersion}; it is a non-authoritative proposal that the runtime will validate and resolve.",
                $"Keep the organizational decision and {AiDirectiveOutcomeProposalEnvelope.PropertyName}.{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.ProposedIntentProperty} compatible: Done maps to Report.Done, Progress maps to Report.Progress or ContinueWork, escalation maps to Escalation or ApprovalRequired, and a child directive maps to Directive.",
                $"Evidence may use only source \"{OutcomeEvidenceSourceContract.ToWireValue(OutcomeEvidenceSource.DirectiveInput)}\" and these exact bounded references: {EvidenceReferenceVocabulary(evidenceContext)}.",
                "A Report.Done proposal requires at least one grounded allowed reference; never invent runtime, tool, completion-criterion, persisted-state, or other evidence.",
            ]);
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private static AiOutputConstraint OutputConstraint(AiDirectiveExecutionContext context)
    {
        var constraint = AiDirectiveDecisionSchema.OutputConstraint;
        return context.RequiresStructuredOutcomeProposal
            ? AiDirectiveOutcomeProposalEnvelope.ComposeOutputConstraint(
                constraint,
                AiDirectiveOutcomeEvidenceContext.CreateProposalContext(context))
            : constraint;
    }

    private static string EvidenceReferenceVocabulary(
        OutcomeProposalEvidenceContext evidenceContext) =>
        evidenceContext.DirectiveInputReferences.IsEmpty
            ? "<empty>"
            : string.Join(
                ", ",
                evidenceContext.DirectiveInputReferences.Select(
                    reference => JsonSerializer.Serialize(reference)));

    private static string BuildRuntimeAuthorityInstruction(
        AiDirectiveExecutionContext context) =>
        string.Join(
            Environment.NewLine,
            [
                "Escalate work outside this position's authority instead of handling it directly.",
                $"Allowed \"{AiToolActingUnderSchema.PropertyName}\" values for this position: {ActingUnderVocabulary(context)}.",
                "Directive only when a permitted downward target is explicit in the provided context.",
                "Do not invent routing, approval, facts, authority, or subordinate positions.",
            ]);

    private static string BuildRuntimeToolsInstruction(
        AiDirectiveExecutionContext context) =>
        string.Join(
            Environment.NewLine,
            [
                "Use only the HIVE tool definitions supplied with this request.",
                $"Authorized connector names: {JoinOrEmpty(context.AuthorizedTools.Select(tool => tool.Connector))}.",
                "Tool availability never extends the position's authority or bypasses approval.",
            ]);

    private static string BuildContent(
        AiDirectiveExecutionContext context,
        AiDirectiveSelectedContext selectedContext)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Directive execution context");
        builder.AppendLine($"CorrelationId: {context.CorrelationId}");
        builder.AppendLine($"OrganizationId: {context.OrganizationId}");
        builder.AppendLine($"PositionId: {context.PositionId}");
        builder.AppendLine($"OccupantId: {context.Occupant}");
        builder.AppendLine($"IdentityPromptRef: {ValueOrNone(context.IdentityPromptRef)}");
        builder.AppendLine($"Provider: {Provider(context)}");
        builder.AppendLine($"ProcessingMode: {ProcessingMode(context)}");
        builder.AppendLine();

        AppendDirective(builder, context);
        AppendAuthority(builder, context);
        AppendTools(builder, context);
        AppendShortMemory(builder, selectedContext.ShortMemory);
        AppendOpenTasks(builder, selectedContext.OpenTasks);
        AppendRecentHistory(builder, selectedContext.RecentHistory);
        AppendRelation(builder, context);
        AppendLimits(builder, context);

        return builder.ToString().TrimEnd();
    }

    private static IdentityPromptRuntimeConfiguration RequireIdentityPrompt(
        AiDirectiveExecutionContext context) =>
        context.IdentityPrompt
        ?? throw new InvalidOperationException(
            "AI directive initial request requires a resolved identity prompt.");

    private static void AppendDirective(StringBuilder builder, AiDirectiveExecutionContext context)
    {
        var directive = context.Directive;

        builder.AppendLine("Directive:");
        builder.AppendLine($"MessageId: {directive.MessageId}");
        builder.AppendLine($"ThreadId: {directive.ThreadId}");
        builder.AppendLine($"DirectiveId: {directive.DirectiveId}");
        builder.AppendLine($"ParentDirectiveId: {ValueOrNone(directive.ParentDirectiveId?.ToString())}");
        builder.AppendLine($"From: {Endpoint(directive.From)}");
        builder.AppendLine($"To: {Endpoint(directive.To)}");
        builder.AppendLine($"Priority: {directive.Priority}");
        builder.AppendLine($"SentAt: {directive.SentAt:O}");
        builder.AppendLine($"Deadline: {ValueOrNone(directive.Deadline?.ToString("O"))}");
        builder.AppendLine($"Objective: {directive.Objective}");
        builder.AppendLine($"Context: {directive.Context}");
        builder.AppendLine();
    }

    private static void AppendAuthority(StringBuilder builder, AiDirectiveExecutionContext context)
    {
        builder.AppendLine("Authority:");
        builder.AppendLine($"CanDecide: {JoinOrEmpty(context.Authority.CanDecide.Select(key => key.Value))}");
        if (context.Authority.Overrides.IsEmpty)
        {
            builder.AppendLine("AuthorityOverrides: <empty>");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("AuthorityOverrides:");
        foreach (var authorityOverride in context.Authority.Overrides)
        {
            builder.AppendLine(
                $"- {authorityOverride.Key.Value}: {GateWireValue(authorityOverride.Gate)} (approver: {ValueOrNone(authorityOverride.Approver)})");
        }

        builder.AppendLine();
    }

    private static void AppendTools(StringBuilder builder, AiDirectiveExecutionContext context)
    {
        if (context.AuthorizedTools.IsEmpty)
        {
            builder.AppendLine("AuthorizedTools: <empty>");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("AuthorizedTools:");
        foreach (var tool in context.AuthorizedTools)
        {
            builder.AppendLine($"- {tool.Connector}: {JoinOrEmpty(tool.Scope)}");
        }

        builder.AppendLine();
    }

    private static void AppendShortMemory(
        StringBuilder builder,
        IReadOnlyList<AiDirectiveShortMemoryEntry> shortMemory)
    {
        if (shortMemory.Count == 0)
        {
            builder.AppendLine("ShortMemory: <empty>");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("ShortMemory:");
        foreach (var entry in shortMemory)
        {
            AppendCanonicalContextLine(builder, AiDirectiveContextLines.ShortMemory(entry));
        }

        builder.AppendLine();
    }

    private static void AppendOpenTasks(
        StringBuilder builder,
        IReadOnlyList<PersistedTask> openTasks)
    {
        if (openTasks.Count == 0)
        {
            builder.AppendLine("OpenTasks: <empty>");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("OpenTasks:");
        foreach (var task in openTasks)
        {
            AppendCanonicalContextLine(builder, AiDirectiveContextLines.Task(task));
        }

        builder.AppendLine();
    }

    private static void AppendRecentHistory(
        StringBuilder builder,
        IReadOnlyList<MessageId> recentHistory)
    {
        if (recentHistory.Count == 0)
        {
            builder.AppendLine("RecentHistory: <empty>");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("RecentHistory:");
        foreach (var message in recentHistory)
        {
            AppendCanonicalContextLine(builder, AiDirectiveContextLines.RecentHistory(message));
        }

        builder.AppendLine();
    }

    private static void AppendCanonicalContextLine(StringBuilder builder, string line) =>
        builder.Append(line).Append('\n');

    private static void AppendRelation(StringBuilder builder, AiDirectiveExecutionContext context)
    {
        builder.AppendLine("OrganizationRelation:");
        builder.AppendLine($"UnitId: {context.Relation.Unit}");
        builder.AppendLine($"ReportsTo: {ValueOrNone(context.Relation.ReportsTo?.ToString())}");
        builder.AppendLine(
            $"PermittedDownwardTargets: {JoinOrEmpty(context.Relation.DirectSubordinates.Select(position => position.Value))}");
        builder.AppendLine();
    }

    private static void AppendLimits(StringBuilder builder, AiDirectiveExecutionContext context)
    {
        builder.AppendLine("Limits:");
        builder.AppendLine($"ContractVersion: {context.Limits.LimitsVersion}");
        builder.AppendLine($"ExecutionTimeout: {ValueOrNone(context.Limits.ExecutionTimeout?.ToString())}");
        builder.AppendLine($"PerCallTimeout: {ValueOrNone(context.Limits.PerCallTimeout?.ToString())}");
        builder.AppendLine($"MaxOutputTokens: {ValueOrNone(context.Limits.MaxOutputTokens?.ToString())}");
        builder.AppendLine($"MaxIterations: {ValueOrNone(context.Limits.MaxIterations?.ToString())}");
        builder.AppendLine($"CostLimits: {(context.Limits.CostLimits is null ? "<none>" : "<configured>")}");
    }

    private static AiModelParameters EffectiveModelParameters(AiDirectiveExecutionContext context) =>
        new(
            context.ModelParameters.Temperature,
            context.Limits.MaxOutputTokens ?? context.ModelParameters.MaxOutputTokens);

    private static IReadOnlyDictionary<string, string> Metadata(AiDirectiveExecutionContext context)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["correlation_id"] = context.CorrelationId,
            ["directive_id"] = context.Directive.DirectiveId.ToString(),
            ["message_id"] = context.Directive.MessageId.ToString(),
            ["iteration"] = "1",
            ["hive.operation"] = "directive-inference",
            ["hive.execution-limits-version"] = context.Limits.LimitsVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        };

        AddTimeoutMetadata(
            metadata,
            "hive.execution-budget-ms",
            context.Limits.ExecutionTimeout);
        AddTimeoutMetadata(
            metadata,
            "hive.per-call-timeout-ms",
            context.Limits.PerCallTimeout);

        if (context.IdentityPromptRef is { } identityPromptRef)
        {
            metadata["identity_prompt_ref"] = identityPromptRef;
        }

        if (context.IdentityPrompt is { } identityPrompt)
        {
            metadata["identity_prompt_path"] = identityPrompt.Path;
        }

        if (context.Limits.MaxIterations is { } maxIterations)
        {
            metadata["max_iterations"] = maxIterations.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private static AiGatewayPolicy? Policy(AiDirectiveExecutionContext context)
    {
        if (context.Provider is null)
        {
            return null;
        }

        return new AiGatewayPolicy(
            [context.Provider],
            hasAvailableBudget: true,
            maxOutputTokens: context.Limits.MaxOutputTokens,
            maxTimeout: context.Limits.PerCallTimeout,
            allowedProcessingModes: context.ProcessingMode is { } mode
                ? [mode]
                : null,
            authorizedTools: context.AuthorizedTools.Select(tool => tool.Connector));
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

    private static IEnumerable<AiToolDefinition> GatewayTools(
        AiDirectiveExecutionContext context)
    {
        if (AiToolActingUnderSchema
            .CanonicalVocabulary(context.Authority.CanDecide)
            .IsEmpty)
        {
            return Enumerable.Empty<AiToolDefinition>();
        }

        return context.AuthorizedTools.Select(tool =>
            AiToolActingUnderSchema.Compose(
                new AiToolDefinition(
                    tool.Connector,
                    $"Authorized HIVE connector '{tool.Connector}' with scopes: {JoinOrEmpty(tool.Scope)}."),
                context.Authority.CanDecide));
    }

    private static string ActingUnderVocabulary(AiDirectiveExecutionContext context)
    {
        var vocabulary = AiToolActingUnderSchema.CanonicalVocabulary(
            context.Authority.CanDecide);

        return vocabulary.IsEmpty
            ? "<empty>"
            : string.Join(", ", vocabulary.Select(value => JsonSerializer.Serialize(value)));
    }

    private static string Endpoint(EndpointRef endpoint) =>
        endpoint switch
        {
            PositionEndpointRef position => $"position:{position.PositionId}",
            OrganizationOwnerEndpointRef => "organization-owner",
            SystemEndpointRef system => $"system:{system.Kind}",
            _ => endpoint.ToString() ?? endpoint.GetType().Name,
        };

    private static string Provider(AiDirectiveExecutionContext context) =>
        context.Provider is null
            ? "<none>"
            : $"{context.Provider.ProviderId}/{context.Provider.ModelId}";

    private static string ProcessingMode(AiDirectiveExecutionContext context) =>
        context.ProcessingMode is { } mode
            ? AiProcessingModeContract.ToWireValue(mode)
            : "<none>";

    private static string GateWireValue(ActionDomainGate gate) =>
        gate switch
        {
            ActionDomainGate.Decide => "decide",
            ActionDomainGate.Escalate => "escalate",
            ActionDomainGate.HumanApproval => "human-approval",
            _ => throw new InvalidOperationException("Unknown action-domain gate."),
        };

    private static string JoinOrEmpty(IEnumerable<string> values)
    {
        var snapshot = values.ToArray();
        return snapshot.Length == 0 ? "<empty>" : string.Join(", ", snapshot);
    }

    private static string ValueOrNone(string? value) => value ?? "<none>";
}

internal sealed record AiDirectiveSystemInstructionSections(
    string BusinessIdentity,
    string HiveProtocol,
    string RuntimeAuthority,
    string RuntimeTools)
{
    internal const string BusinessIdentityHeader =
        "## Business identity [owner: organization]";
    internal const string HiveProtocolHeader =
        "## HIVE protocol [owner: runtime]";
    internal const string RuntimeAuthorityHeader =
        "## Runtime authority [owner: runtime]";
    internal const string RuntimeToolsHeader =
        "## Runtime tools [owner: runtime]";
    public string Compose()
    {
        var sections = new List<string>
        {
            Section(BusinessIdentityHeader, BusinessIdentity),
            Section(HiveProtocolHeader, HiveProtocol),
            Section(RuntimeAuthorityHeader, RuntimeAuthority),
            Section(RuntimeToolsHeader, RuntimeTools),
        };
        return string.Join($"{Environment.NewLine}{Environment.NewLine}", sections);
    }

    private static string Section(string header, string content) =>
        string.Join(Environment.NewLine, header, content.Trim());
}

internal sealed record GetAiDirectiveInitialPrompt
{
    public GetAiDirectiveInitialPrompt(string correlationId)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

internal sealed record AiDirectiveInitialPromptQueryResult
{
    private AiDirectiveInitialPromptQueryResult(
        string correlationId,
        AiGatewayRequest? request)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Request = request;
    }

    public string CorrelationId { get; }

    public AiGatewayRequest? Request { get; }

    public bool Found => Request is not null;

    public static AiDirectiveInitialPromptQueryResult FoundRequest(
        string correlationId,
        AiGatewayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AiDirectiveInitialPromptQueryResult(correlationId, request);
    }

    public static AiDirectiveInitialPromptQueryResult Missing(string correlationId) =>
        new(correlationId, request: null);
}
