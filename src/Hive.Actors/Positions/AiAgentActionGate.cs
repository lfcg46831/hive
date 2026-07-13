using System.Collections.Immutable;
using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Governance;

namespace Hive.Actors.Positions;

internal enum AiAgentActionGateOutcome
{
    Allowed = 1,
    RetainedForEscalation = 2,
    RetainedForHumanApproval = 3,
}

internal static class AiAgentActionGateCodes
{
    public const string UnknownFailure = "action-gate-failure";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        ActionGateResolution.DeclaredAuthorityCode,
        ActionGateResolution.ObjectiveEscalationCode,
        ActionGateResolution.ObjectiveHumanApprovalCode,
        ActionGateResolution.UnmatchedActionDefaultCode,
        "action-gate-contract-unavailable",
        "action-gate-extractor-binding-invalid",
        "action-gate-direct-facts-invalid",
        "action-gate-action-contract-mismatch",
        "action-gate-action-extractor-unexpected",
        "action-gate-action-extractor-missing",
        "action-gate-action-extractor-contract-mismatch",
        "action-gate-action-attribute-extractor-threw",
        "action-gate-action-attribute-extractor-returned-null",
        "action-gate-action-attribute-extractor-invalid-input",
        "action-gate-action-attribute-extractor-classification-unavailable",
        "action-gate-action-attribute-extractor-configuration-unavailable",
        "action-gate-direct-attribute-selector-collision",
        "action-gate-direct-attribute-not-declared",
        "action-gate-direct-derived-attribute-collision",
        "action-gate-direct-attribute-type-mismatch",
        "action-gate-direct-attribute-value-not-allowed",
        "action-gate-direct-attribute-missing",
        "action-gate-derived-attribute-unexpected",
        "action-gate-derived-direct-attribute-collision",
        "action-gate-derived-attribute-type-mismatch",
        "action-gate-derived-attribute-value-not-allowed",
        "action-gate-derived-attribute-missing",
        "action-gate-outcome-invalid",
        "action-gate-evaluation-failed",
        "action-gate-approval-resolution-failed",
        "action-gate-approver-unresolved",
        "action-gate-approver-mismatch",
        "action-gate-approval-resolver-unavailable",
        "action-gate-policy-not-found",
        "action-gate-catalog-unavailable",
        "action-gate-snapshot-divergent",
        "action-gate-binding-invalid",
    };

    public static string Normalize(string code)
    {
        var required = AiAgentGatewayText.Require(code, nameof(code));
        return Known.Contains(required) ? required : UnknownFailure;
    }
}

internal sealed record AiAgentActionCandidate
{
    private AiAgentActionCandidate(
        ActionDomainActionKind kind,
        string selector,
        ActingUnderDeclaration actingUnder,
        AiToolCall? toolCall,
        OrgMessage? message)
    {
        Kind = kind;
        Selector = AiAgentGatewayText.Require(selector, nameof(selector));
        ActingUnder = actingUnder ?? throw new ArgumentNullException(nameof(actingUnder));
        ToolCall = toolCall;
        Message = message;
    }

    public ActionDomainActionKind Kind { get; }

    public string Selector { get; }

    public ActingUnderDeclaration ActingUnder { get; }

    public AiToolCall? ToolCall { get; }

    public OrgMessage? Message { get; }

    public static AiAgentActionCandidate ForTool(
        AiToolCall toolCall,
        ActingUnderDeclaration actingUnder)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        return new AiAgentActionCandidate(
            ActionDomainActionKind.Tool,
            toolCall.Name,
            actingUnder,
            toolCall,
            message: null);
    }

    public static AiAgentActionCandidate ForMessage(
        OrgMessage message,
        ActingUnderDeclaration actingUnder)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new AiAgentActionCandidate(
            ActionDomainActionKind.OrganizationalMessage,
            MessageSelector(message),
            actingUnder,
            toolCall: null,
            message);
    }

    private static string MessageSelector(OrgMessage message) =>
        message switch
        {
            Report => nameof(Report),
            Escalation => nameof(Escalation),
            Directive => nameof(Directive),
            ApprovalRequest => nameof(ApprovalRequest),
            ApprovalDecision => nameof(ApprovalDecision),
            AuthorizationGrant => AuthorizationGrantAuthority.MessageSelector,
            _ => throw new ArgumentException(
                $"Organizational message type '{message.GetType().Name}' has no action-domain selector.",
                nameof(message)),
        };
}

internal sealed record AiAgentActionRetentionIntent
{
    public AiAgentActionRetentionIntent(
        AiAgentActionCandidate candidate,
        string correlationId,
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId sourceMessageId,
        DirectiveId directiveId,
        DirectiveId? parentDirectiveId,
        string code,
        IEnumerable<OrgMessage> governanceMessages)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        SourceMessageId = sourceMessageId ?? throw new ArgumentNullException(nameof(sourceMessageId));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        ParentDirectiveId = parentDirectiveId;
        Code = AiAgentActionGateCodes.Normalize(code);
        ArgumentNullException.ThrowIfNull(governanceMessages);
        GovernanceMessages = governanceMessages.ToImmutableArray();
        if (GovernanceMessages.IsDefaultOrEmpty || GovernanceMessages.Any(message => message is null))
        {
            throw new ArgumentException(
                "A retained AI action must materialize at least one governance message.",
                nameof(governanceMessages));
        }
    }

    public AiAgentActionCandidate Candidate { get; }

    public string CorrelationId { get; }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public ThreadId ThreadId { get; }

    public MessageId SourceMessageId { get; }

    public DirectiveId DirectiveId { get; }

    public DirectiveId? ParentDirectiveId { get; }

    public string Code { get; }

    public ImmutableArray<OrgMessage> GovernanceMessages { get; }
}

internal sealed record AiAgentActionGateResult
{
    private AiAgentActionGateResult(
        AiAgentActionGateOutcome outcome,
        AiAgentActionCandidate candidate,
        ActionFacts? facts,
        ActionGateResolution? resolution,
        string code,
        AiAgentActionRetentionIntent? retention)
    {
        Outcome = outcome;
        Candidate = candidate;
        Facts = facts;
        Resolution = resolution;
        Code = code;
        Retention = retention;
    }

    public AiAgentActionGateOutcome Outcome { get; }

    public AiAgentActionCandidate Candidate { get; }

    public ActionFacts? Facts { get; }

    public ActionGateResolution? Resolution { get; }

    public string Code { get; }

    public AiAgentActionRetentionIntent? Retention { get; }

    public bool IsAllowed => Outcome == AiAgentActionGateOutcome.Allowed;

    public bool IsRetained => !IsAllowed;

    public static AiAgentActionGateResult Allowed(
        AiAgentActionCandidate candidate,
        ActionFacts? facts = null,
        ActionGateResolution? resolution = null) =>
        new(
            AiAgentActionGateOutcome.Allowed,
            candidate ?? throw new ArgumentNullException(nameof(candidate)),
            facts,
            resolution,
            resolution?.Code ?? "action-gate-allowed",
            retention: null);

    public static AiAgentActionGateResult Retained(
        AiAgentActionGateOutcome outcome,
        AiAgentActionCandidate candidate,
        ActionFacts? facts,
        ActionGateResolution? resolution,
        string code,
        AiAgentActionRetentionIntent retention)
    {
        if (outcome == AiAgentActionGateOutcome.Allowed || !Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        return new AiAgentActionGateResult(
            outcome,
            candidate ?? throw new ArgumentNullException(nameof(candidate)),
            facts,
            resolution,
            AiAgentActionGateCodes.Normalize(code),
            retention ?? throw new ArgumentNullException(nameof(retention)));
    }
}

internal sealed record GetAiAgentActionGateResult
{
    public GetAiAgentActionGateResult(string correlationId)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

internal sealed record AiAgentActionGateQueryResult(
    string CorrelationId,
    AiAgentActionGateResult? Result)
{
    public bool Found => Result is not null;
}

internal interface IAiAgentActionGate
{
    ValueTask<AiAgentActionGateResult> EvaluateAsync(
        AiDirectiveExecutionContext context,
        AiAgentActionCandidate candidate,
        CancellationToken cancellationToken = default);
}

internal sealed record AiActionApprovalResolutionQuery(
    OrganizationId OrganizationId,
    PositionId Requester,
    string? RequiredApprover,
    ImmutableArray<AuthorityKey> AuthorityKeys);

internal sealed record AiActionApprovalResolution
{
    private AiActionApprovalResolution(
        EndpointRef? approver,
        ApprovalPolicyRef? policy,
        string? failureCode)
    {
        Approver = approver;
        Policy = policy;
        FailureCode = failureCode;
    }

    public EndpointRef? Approver { get; }

    public ApprovalPolicyRef? Policy { get; }

    public string? FailureCode { get; }

    public bool IsResolved => Approver is not null && Policy is not null;

    public static AiActionApprovalResolution Resolved(
        EndpointRef approver,
        ApprovalPolicyRef policy) =>
        new(
            approver ?? throw new ArgumentNullException(nameof(approver)),
            policy ?? throw new ArgumentNullException(nameof(policy)),
            failureCode: null);

    public static AiActionApprovalResolution Failed(string code) =>
        new(
            approver: null,
            policy: null,
            AiAgentActionGateCodes.Normalize(code));
}

internal interface IAiActionApprovalResolver
{
    ValueTask<AiActionApprovalResolution> ResolveAsync(
        AiActionApprovalResolutionQuery query,
        CancellationToken cancellationToken = default);
}

internal sealed class AiAgentActionGate : AiAgentActionGateBase
{
    private readonly ActionDomainCatalog? _catalog;
    private readonly ActionDomainCatalogBinding? _binding;
    private readonly IOrganizationActionGateRuntimeProvider? _runtimeProvider;
    private readonly IAiActionApprovalResolver _approvalResolver;
    private readonly Func<DateTimeOffset> _clock;

    public AiAgentActionGate(
        ActionDomainCatalog catalog,
        ActionDomainCatalogBinding binding,
        IAiActionApprovalResolver approvalResolver,
        Func<DateTimeOffset>? clock = null)
        : this(
            catalog,
            binding,
            approvalResolver,
            NoopJourneyAuditLog.Instance,
            clock)
    {
    }

    public AiAgentActionGate(
        ActionDomainCatalog catalog,
        ActionDomainCatalogBinding binding,
        IAiActionApprovalResolver approvalResolver,
        IJourneyAuditLog auditLog,
        Func<DateTimeOffset>? clock = null,
        Func<DateTimeOffset>? auditClock = null)
        : base(auditLog, auditClock)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _runtimeProvider = null;
        _approvalResolver = approvalResolver ?? throw new ArgumentNullException(nameof(approvalResolver));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public AiAgentActionGate(
        IOrganizationActionGateRuntimeProvider runtimeProvider,
        IAiActionApprovalResolver approvalResolver,
        IJourneyAuditLog auditLog,
        Func<DateTimeOffset>? clock = null,
        Func<DateTimeOffset>? auditClock = null)
        : base(auditLog, auditClock)
    {
        _runtimeProvider = runtimeProvider
            ?? throw new ArgumentNullException(nameof(runtimeProvider));
        _approvalResolver = approvalResolver
            ?? throw new ArgumentNullException(nameof(approvalResolver));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal static AiAgentActionGate CreateFailClosed(
        IJourneyAuditLog auditLog,
        Func<DateTimeOffset>? clock = null,
        Func<DateTimeOffset>? auditClock = null) =>
        new(
            new ActionDomainCatalog(
                1,
                new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
                []),
            new ActionDomainCatalogBinding(),
            RejectingApprovalResolver.Instance,
            auditLog,
            clock,
            auditClock);

    internal static AiAgentActionGate CreateRuntime(
        IOrganizationActionGateRuntimeProvider runtimeProvider,
        IJourneyAuditLog auditLog,
        Func<DateTimeOffset>? clock = null,
        Func<DateTimeOffset>? auditClock = null) =>
        new(
            runtimeProvider,
            RejectingApprovalResolver.Instance,
            auditLog,
            clock,
            auditClock);

    protected override async ValueTask<AiAgentActionGateResult> EvaluateCoreAsync(
        AiDirectiveExecutionContext context,
        AiAgentActionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            ActionDomainCatalog catalog;
            ActionDomainCatalogBinding binding;
            ActionDomainAuthorityBinding authority;
            if (_runtimeProvider is null)
            {
                catalog = _catalog!;
                binding = _binding!;
                authority = Authority(context);
            }
            else
            {
                OrganizationActionGateRuntimeSnapshot? runtime;
                try
                {
                    runtime = await _runtimeProvider
                        .FindAsync(context.OrganizationId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    return RetainFailure(context, candidate, "action-gate-catalog-unavailable");
                }

                if (runtime is null)
                {
                    return RetainFailure(context, candidate, "action-gate-catalog-unavailable");
                }

                if (context.LastConfigurationStamp is not { } stamp
                    || stamp.Version != runtime.Version
                    || !string.Equals(stamp.Fingerprint, runtime.Fingerprint, StringComparison.Ordinal))
                {
                    return RetainFailure(context, candidate, "action-gate-snapshot-divergent");
                }

                catalog = runtime.Catalog;
                binding = runtime.Binding;
                var authorityPath = $"positions[{context.PositionId.Value}].authority";
                var authorities = binding.Authorities
                    .Where(item => string.Equals(item.Path, authorityPath, StringComparison.Ordinal))
                    .ToArray();
                if (authorities.Length != 1 || !AuthorityMatches(authorities[0], context.Authority))
                {
                    return RetainFailure(context, candidate, "action-gate-binding-invalid");
                }

                authority = authorities[0];
            }

            var contracts = binding.ActionContracts
                .Where(contract => contract.Action == candidate.Kind
                                   && string.Equals(
                                       contract.SelectorValue,
                                       candidate.Selector,
                                       StringComparison.Ordinal))
                .ToArray();
            if (contracts.Length != 1)
            {
                return RetainFailure(
                    context,
                    candidate,
                    "action-gate-contract-unavailable");
            }

            var contract = contracts[0];
            var registrations = binding.ActionExtractors
                .Where(registration => registration.Action == candidate.Kind
                                       && string.Equals(
                                           registration.SelectorValue,
                                           candidate.Selector,
                                           StringComparison.Ordinal))
                .ToArray();
            if (registrations.Length > 1)
            {
                return RetainFailure(
                    context,
                    candidate,
                    "action-gate-extractor-binding-invalid");
            }

            var directAttributes = DirectAttributes(contract, candidate);
            if (directAttributes is null)
            {
                return RetainFailure(
                    context,
                    candidate,
                    "action-gate-direct-facts-invalid");
            }

            var extraction = ActionAttributeExtractorRunner.Extract(
                contract,
                registrations.SingleOrDefault(),
                new ActionAttributeExtractionRequest(
                    candidate.Kind,
                    candidate.Selector,
                    directAttributes));
            if (!extraction.IsSuccess)
            {
                return RetainFailure(
                    context,
                    candidate,
                    "action-gate-" + extraction.Failure!.Code);
            }

            var facts = extraction.Facts!;
            var resolution = ActionGateResolver.Resolve(
                catalog,
                authority,
                facts,
                candidate.ActingUnder);

            return resolution.Outcome switch
            {
                ActionGateOutcome.Allowed =>
                    AiAgentActionGateResult.Allowed(candidate, facts, resolution),
                ActionGateOutcome.EscalationRequired =>
                    RetainEscalation(context, candidate, facts, resolution, resolution.Code),
                ActionGateOutcome.HumanApprovalRequired =>
                    await RetainForApprovalAsync(
                        context,
                        candidate,
                        facts,
                        resolution,
                        cancellationToken).ConfigureAwait(false),
                _ => RetainFailure(context, candidate, "action-gate-outcome-invalid", facts),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return RetainFailure(context, candidate, "action-gate-evaluation-failed");
        }
    }

    private async ValueTask<AiAgentActionGateResult> RetainForApprovalAsync(
        AiDirectiveExecutionContext context,
        AiAgentActionCandidate candidate,
        ActionFacts facts,
        ActionGateResolution resolution,
        CancellationToken cancellationToken)
    {
        var resolved = new List<(ActionGateApprovalRequirement Requirement, AiActionApprovalResolution Resolution)>();
        foreach (var requirement in resolution.RequiredApprovals)
        {
            AiActionApprovalResolution approval;
            try
            {
                approval = await _approvalResolver.ResolveAsync(
                    new AiActionApprovalResolutionQuery(
                        context.OrganizationId,
                        context.PositionId,
                        requirement.Approver,
                        requirement.AuthorityKeys),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return RetainEscalation(
                    context,
                    candidate,
                    facts,
                    resolution,
                    "action-gate-approval-resolution-failed",
                    AiAgentActionGateOutcome.RetainedForHumanApproval);
            }

            if (approval is null || !approval.IsResolved)
            {
                return RetainEscalation(
                    context,
                    candidate,
                    facts,
                    resolution,
                    approval?.FailureCode ?? "action-gate-approver-unresolved",
                    AiAgentActionGateOutcome.RetainedForHumanApproval);
            }

            if (requirement.Approver is { } expected
                && (approval.Approver is not PositionEndpointRef resolvedApprover
                    || !string.Equals(
                        resolvedApprover.PositionId.Value,
                        expected,
                        StringComparison.Ordinal)))
            {
                return RetainEscalation(
                    context,
                    candidate,
                    facts,
                    resolution,
                    "action-gate-approver-mismatch",
                    AiAgentActionGateOutcome.RetainedForHumanApproval);
            }

            resolved.Add((requirement, approval));
        }

        var messages = resolved
            .Select((item, index) => (OrgMessage)new ApprovalRequest(
                NewMessageId(context, candidate, $"approval:{index}"),
                context.OrganizationId,
                new PositionEndpointRef(context.PositionId),
                item.Resolution.Approver!,
                context.Directive.ThreadId,
                context.Directive.Priority,
                schemaVersion: 1,
                _clock(),
                context.Directive.Deadline,
                ActionDescriptor(candidate),
                $"Authority gate requires approval for {AuthorityKeyList(item.Requirement.AuthorityKeys)}.",
                item.Resolution.Policy!))
            .ToImmutableArray();

        var retention = Retention(context, candidate, resolution.Code, messages);
        return AiAgentActionGateResult.Retained(
            AiAgentActionGateOutcome.RetainedForHumanApproval,
            candidate,
            facts,
            resolution,
            resolution.Code,
            retention);
    }

    private AiAgentActionGateResult RetainFailure(
        AiDirectiveExecutionContext context,
        AiAgentActionCandidate candidate,
        string code,
        ActionFacts? facts = null) =>
        RetainEscalation(
            context,
            candidate,
            facts,
            resolution: null,
            code);

    private AiAgentActionGateResult RetainEscalation(
        AiDirectiveExecutionContext context,
        AiAgentActionCandidate candidate,
        ActionFacts? facts,
        ActionGateResolution? resolution,
        string code,
        AiAgentActionGateOutcome outcome = AiAgentActionGateOutcome.RetainedForEscalation)
    {
        var safeCode = AiAgentActionGateCodes.Normalize(code);
        EndpointRef destination = context.Relation.ReportsTo is { } superior
            ? new PositionEndpointRef(superior)
            : new OrganizationOwnerEndpointRef();
        var escalation = new Escalation(
            NewMessageId(context, candidate, "escalation"),
            context.OrganizationId,
            new PositionEndpointRef(context.PositionId),
            destination,
            context.Directive.ThreadId,
            context.Directive.Priority,
            schemaVersion: 1,
            _clock(),
            context.Directive.Deadline,
            "Action retained by authority gate.",
            $"{ActionDescriptor(candidate)} was retained with code '{safeCode}'.",
            []);
        var retention = Retention(context, candidate, safeCode, [escalation]);

        return AiAgentActionGateResult.Retained(
            outcome,
            candidate,
            facts,
            resolution,
            safeCode,
            retention);
    }

    private static IReadOnlyDictionary<string, ActionAttributeValue>? DirectAttributes(
        ActionDomainActionContract contract,
        AiAgentActionCandidate candidate)
    {
        var values = new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal);
        foreach (var definition in contract.Attributes.Where(definition =>
                     definition.Source == ActionAttributeSource.Direct
                     && !string.Equals(
                         definition.Name,
                         contract.SelectorAttribute,
                         StringComparison.Ordinal)))
        {
            if (candidate.ToolCall is null
                || !candidate.ToolCall.Arguments.TryGetValue(definition.Name, out var raw)
                || !TryScalar(raw, out var value))
            {
                return null;
            }

            values.Add(definition.Name, value!);
        }

        return values;
    }

    private static bool TryScalar(object? raw, out ActionAttributeValue? value)
    {
        if (raw is JsonElement element)
        {
            raw = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
                _ => null,
            };
        }

        return ActionAttributeValue.TryFromScalar(raw, out value);
    }

    private static ActionDomainAuthorityBinding Authority(AiDirectiveExecutionContext context) =>
        new(
            $"positions[{context.PositionId.Value}].authority",
            context.Authority.CanDecide,
            context.Authority.Overrides.Select(item => new ActionDomainAuthorityOverride(
                    item.Key,
                    item.Gate,
                    item.Approver))
                .ToArray());

    private static bool AuthorityMatches(
        ActionDomainAuthorityBinding binding,
        AiDirectiveExecutionAuthority authority) =>
        binding.CanDecide.Select(key => key.Value).OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(authority.CanDecide.Select(key => key.Value).OrderBy(value => value, StringComparer.Ordinal))
        && binding.Overrides
            .Select(item => $"{item.Key.Value}|{item.Gate}|{item.Approver}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(authority.Overrides
                .Select(item => $"{item.Key.Value}|{item.Gate}|{item.Approver}")
                .OrderBy(value => value, StringComparer.Ordinal));

    private static AiAgentActionRetentionIntent Retention(
        AiDirectiveExecutionContext context,
        AiAgentActionCandidate candidate,
        string code,
        IEnumerable<OrgMessage> messages) =>
        new(
            candidate,
            context.CorrelationId,
            context.OrganizationId,
            context.PositionId,
            context.Directive.ThreadId,
            context.Directive.MessageId,
            context.Directive.DirectiveId,
            context.Directive.ParentDirectiveId,
            code,
            messages);

    private static string ActionDescriptor(AiAgentActionCandidate candidate) =>
        candidate.Kind == ActionDomainActionKind.Tool
            ? $"tool:{candidate.Selector}"
            : $"organizational-message:{candidate.Selector}";

    private static string AuthorityKeyList(IEnumerable<AuthorityKey> keys) =>
        string.Join(",", keys.Select(key => key.Value));

    private static MessageId NewMessageId(
        AiDirectiveExecutionContext context,
        AiAgentActionCandidate candidate,
        string slot) =>
        MessageId.From(DeterministicGuid.FromName(string.Join(
            "|",
            "ai-action-gate:v1",
            context.OrganizationId.Value,
            context.PositionId.Value,
            context.Directive.ThreadId.Value.ToString("N"),
            context.Directive.DirectiveId.Value.ToString("N"),
            candidate.Kind,
            candidate.Selector,
            slot)));

    private sealed class RejectingApprovalResolver : IAiActionApprovalResolver
    {
        public static RejectingApprovalResolver Instance { get; } = new();

        public ValueTask<AiActionApprovalResolution> ResolveAsync(
            AiActionApprovalResolutionQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                AiActionApprovalResolution.Failed("action-gate-approval-resolver-unavailable"));
    }
}

// Compatibility bypass for focused actor/unit constructors. It is never registered by the
// production composition root, whose gate must derive from AiAgentActionGateBase.
internal sealed class AllowingAiAgentActionGate : IAiAgentActionGate
{
    public static AllowingAiAgentActionGate Instance { get; } = new();

    private AllowingAiAgentActionGate()
    {
    }

    public ValueTask<AiAgentActionGateResult> EvaluateAsync(
        AiDirectiveExecutionContext context,
        AiAgentActionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AiAgentActionGateResult.Allowed(candidate));
    }
}
