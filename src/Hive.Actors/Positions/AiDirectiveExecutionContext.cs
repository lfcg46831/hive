using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

internal sealed record AiDirectiveExecutionContext
{
    private AiDirectiveExecutionContext(
        string correlationId,
        OrganizationId organizationId,
        PositionId positionId,
        OccupantId occupant,
        string? identityPromptRef,
        AiDirectiveExecutionDirective directive,
        AiDirectiveExecutionRelation relation,
        AiDirectiveExecutionAuthority authority,
        ImmutableArray<AiDirectiveExecutionTool> authorizedTools,
        ImmutableArray<AiDirectiveShortMemoryEntry> shortMemory,
        ImmutableArray<PersistedTask> openTasks,
        ImmutableArray<MessageId> recentHistory,
        AiDirectiveProcessingLimits limits,
        PositionConfigurationStamp? lastConfigurationStamp)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        IdentityPromptRef = identityPromptRef is null
            ? null
            : AiAgentGatewayText.Require(identityPromptRef, nameof(identityPromptRef));
        Directive = directive ?? throw new ArgumentNullException(nameof(directive));
        Relation = relation ?? throw new ArgumentNullException(nameof(relation));
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        AuthorizedTools = RequireItems(authorizedTools, nameof(authorizedTools));
        ShortMemory = RequireItems(shortMemory, nameof(shortMemory));
        OpenTasks = RequireItems(openTasks, nameof(openTasks));
        RecentHistory = RequireItems(recentHistory, nameof(recentHistory));
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        LastConfigurationStamp = lastConfigurationStamp;
    }

    public string CorrelationId { get; }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public OccupantId Occupant { get; }

    public string? IdentityPromptRef { get; }

    public AiDirectiveExecutionDirective Directive { get; }

    public AiDirectiveExecutionRelation Relation { get; }

    public AiDirectiveExecutionAuthority Authority { get; }

    public ImmutableArray<AiDirectiveExecutionTool> AuthorizedTools { get; }

    public ImmutableArray<AiDirectiveShortMemoryEntry> ShortMemory { get; }

    public ImmutableArray<PersistedTask> OpenTasks { get; }

    public ImmutableArray<MessageId> RecentHistory { get; }

    public AiDirectiveProcessingLimits Limits { get; }

    public PositionConfigurationStamp? LastConfigurationStamp { get; }

    public static AiDirectiveExecutionContext From(AiDirectiveProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AiDirectiveExecutionContext(
            request.CorrelationId,
            request.OrganizationId,
            request.PositionId,
            request.Occupant,
            request.RuntimeContext.OccupantConfiguration.IdentityPromptRef,
            AiDirectiveExecutionDirective.FromDirective(request.Directive),
            new AiDirectiveExecutionRelation(
                request.RuntimeContext.Position.Unit,
                request.RuntimeContext.Position.ReportsTo),
            AiDirectiveExecutionAuthority.From(request.RuntimeContext.Authority),
            request.RuntimeContext.OccupantConfiguration.Tools
                .Select(AiDirectiveExecutionTool.From)
                .ToImmutableArray(),
            request.PersistedContext.ShortMemory
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new AiDirectiveShortMemoryEntry(entry.Key, entry.Value))
                .ToImmutableArray(),
            request.PersistedContext.OpenTasks
                .OrderBy(task => task.TaskId.Value)
                .ToImmutableArray(),
            request.PersistedContext.RecentHistory,
            request.Limits,
            request.PersistedContext.LastConfigurationStamp);
    }

    private static ImmutableArray<T> RequireItems<T>(
        ImmutableArray<T> values,
        string parameterName)
        where T : class
    {
        if (values.IsDefault)
        {
            throw new ArgumentException("Collection cannot be default.", parameterName);
        }

        foreach (var value in values)
        {
            if (value is null)
            {
                throw new ArgumentException("Collection cannot contain null items.", parameterName);
            }
        }

        return values;
    }
}

internal sealed record AiDirectiveExecutionDirective
{
    private AiDirectiveExecutionDirective(
        MessageId messageId,
        ThreadId threadId,
        DirectiveId directiveId,
        DirectiveId? parentDirectiveId,
        EndpointRef from,
        EndpointRef to,
        Priority priority,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        string objective,
        string context)
    {
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        ParentDirectiveId = parentDirectiveId;
        From = from ?? throw new ArgumentNullException(nameof(from));
        To = to ?? throw new ArgumentNullException(nameof(to));
        Priority = PriorityContract.RequireDefined(priority, nameof(priority));
        SentAt = sentAt;
        Deadline = deadline;
        Objective = AiAgentGatewayText.Require(objective, nameof(objective));
        Context = AiAgentGatewayText.Require(context, nameof(context));
    }

    public MessageId MessageId { get; }

    public ThreadId ThreadId { get; }

    public DirectiveId DirectiveId { get; }

    public DirectiveId? ParentDirectiveId { get; }

    public EndpointRef From { get; }

    public EndpointRef To { get; }

    public Priority Priority { get; }

    public DateTimeOffset SentAt { get; }

    public DateTimeOffset? Deadline { get; }

    public string Objective { get; }

    public string Context { get; }

    public static AiDirectiveExecutionDirective FromDirective(Directive directive)
    {
        ArgumentNullException.ThrowIfNull(directive);

        return new AiDirectiveExecutionDirective(
            directive.Id,
            directive.Thread,
            directive.DirectiveId,
            directive.ParentDirectiveId,
            directive.From,
            directive.To,
            directive.Priority,
            directive.SentAt,
            directive.Deadline,
            directive.Objective,
            directive.Context);
    }
}

internal sealed record AiDirectiveExecutionRelation
{
    public AiDirectiveExecutionRelation(UnitId unit, PositionId? reportsTo)
    {
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));
        ReportsTo = reportsTo;
    }

    public UnitId Unit { get; }

    public PositionId? ReportsTo { get; }
}

internal sealed record AiDirectiveExecutionAuthority
{
    private AiDirectiveExecutionAuthority(
        ImmutableArray<string> canDecide,
        ImmutableArray<string> mustEscalate,
        ImmutableArray<string> requiresHumanApproval)
    {
        CanDecide = RequireTextItems(canDecide, nameof(canDecide));
        MustEscalate = RequireTextItems(mustEscalate, nameof(mustEscalate));
        RequiresHumanApproval = RequireTextItems(
            requiresHumanApproval,
            nameof(requiresHumanApproval));
    }

    public ImmutableArray<string> CanDecide { get; }

    public ImmutableArray<string> MustEscalate { get; }

    public ImmutableArray<string> RequiresHumanApproval { get; }

    public static AiDirectiveExecutionAuthority From(
        PositionAuthorityRuntimeConfiguration authority)
    {
        ArgumentNullException.ThrowIfNull(authority);

        return new AiDirectiveExecutionAuthority(
            authority.CanDecide,
            authority.MustEscalate,
            authority.RequiresHumanApproval);
    }

    private static ImmutableArray<string> RequireTextItems(
        ImmutableArray<string> values,
        string parameterName)
    {
        if (values.IsDefault)
        {
            throw new ArgumentException("Collection cannot be default.", parameterName);
        }

        foreach (var value in values)
        {
            AiAgentGatewayText.Require(value, parameterName);
        }

        return values;
    }
}

internal sealed record AiDirectiveExecutionTool
{
    private AiDirectiveExecutionTool(string connector, ImmutableArray<string> scope)
    {
        Connector = AiAgentGatewayText.Require(connector, nameof(connector));
        Scope = RequireScope(scope, nameof(scope));
    }

    public string Connector { get; }

    public ImmutableArray<string> Scope { get; }

    public static AiDirectiveExecutionTool From(
        Hive.Domain.Organization.Configuration.ToolConfiguration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return new AiDirectiveExecutionTool(
            tool.Connector,
            tool.Scope.Select(scope => AiAgentGatewayText.Require(
                    scope,
                    nameof(tool.Scope)))
                .ToImmutableArray());
    }

    private static ImmutableArray<string> RequireScope(
        ImmutableArray<string> values,
        string parameterName)
    {
        if (values.IsDefault)
        {
            throw new ArgumentException("Collection cannot be default.", parameterName);
        }

        foreach (var value in values)
        {
            AiAgentGatewayText.Require(value, parameterName);
        }

        return values;
    }
}

internal sealed record AiDirectiveShortMemoryEntry
{
    public AiDirectiveShortMemoryEntry(string key, string value)
    {
        Key = AiAgentGatewayText.Require(key, nameof(key));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Key { get; }

    public string Value { get; }
}

internal sealed record GetAiDirectiveExecutionContext
{
    public GetAiDirectiveExecutionContext(string correlationId)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

internal sealed record AiDirectiveExecutionContextQueryResult
{
    private AiDirectiveExecutionContextQueryResult(
        string correlationId,
        AiDirectiveExecutionContext? context)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Context = context;
    }

    public string CorrelationId { get; }

    public AiDirectiveExecutionContext? Context { get; }

    public bool Found => Context is not null;

    public static AiDirectiveExecutionContextQueryResult FoundContext(
        AiDirectiveExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new AiDirectiveExecutionContextQueryResult(
            context.CorrelationId,
            context);
    }

    public static AiDirectiveExecutionContextQueryResult Missing(string correlationId) =>
        new(correlationId, context: null);
}
