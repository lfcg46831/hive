using System.Collections.Immutable;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

internal sealed record AiDirectiveProcessingRequest
{
    private AiDirectiveProcessingRequest(
        AiDirectiveRuntimeContext runtimeContext,
        Directive directive,
        AiDirectiveProcessingLimits limits,
        AiDirectivePersistedContext persistedContext,
        string correlationId)
    {
        RuntimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        Directive = directive ?? throw new ArgumentNullException(nameof(directive));
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        PersistedContext = persistedContext ?? throw new ArgumentNullException(nameof(persistedContext));
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public AiDirectiveRuntimeContext RuntimeContext { get; }

    public Directive Directive { get; }

    public AiDirectiveProcessingLimits Limits { get; }

    public AiDirectivePersistedContext PersistedContext { get; }

    public string CorrelationId { get; }

    public OrganizationId OrganizationId => RuntimeContext.PositionEntityId.Organization;

    public PositionId PositionId => RuntimeContext.PositionEntityId.Position;

    public OccupantId Occupant => RuntimeContext.Occupant;

    public ThreadId ThreadId => Directive.Thread;

    public DirectiveId DirectiveId => Directive.DirectiveId;

    public MessageId MessageId => Directive.Id;

    public static AiDirectiveProcessingRequest Create(
        PositionEntityId entityId,
        PositionRuntimeConfiguration runtimeConfiguration,
        PositionState persistedState,
        OccupantId occupant,
        Directive directive)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        ArgumentNullException.ThrowIfNull(runtimeConfiguration);
        ArgumentNullException.ThrowIfNull(persistedState);
        ArgumentNullException.ThrowIfNull(occupant);
        ArgumentNullException.ThrowIfNull(directive);

        if (!runtimeConfiguration.Matches(entityId))
        {
            throw new ArgumentException(
                "Runtime configuration must match the position entity.",
                nameof(runtimeConfiguration));
        }

        return new AiDirectiveProcessingRequest(
            new AiDirectiveRuntimeContext(
                entityId,
                runtimeConfiguration.Position,
                occupant,
                runtimeConfiguration.Occupant,
                runtimeConfiguration.Authority),
            directive,
            AiDirectiveProcessingLimits.From(runtimeConfiguration.Occupant.AiGateway),
            AiDirectivePersistedContext.From(persistedState),
            CreateCorrelationId(directive));
    }

    private static string CreateCorrelationId(Directive directive) =>
        $"directive:{directive.DirectiveId.Value:N}:message:{directive.Id.Value:N}";
}

internal sealed record AiDirectiveRuntimeContext
{
    public AiDirectiveRuntimeContext(
        PositionEntityId positionEntityId,
        PositionRuntimeDescriptor position,
        OccupantId occupant,
        OccupantRuntimeConfiguration occupantConfiguration,
        PositionAuthorityRuntimeConfiguration authority)
    {
        ArgumentNullException.ThrowIfNull(positionEntityId);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(occupant);
        ArgumentNullException.ThrowIfNull(occupantConfiguration);
        ArgumentNullException.ThrowIfNull(authority);

        if (occupantConfiguration.Type != OccupantType.AiAgent)
        {
            throw new ArgumentException(
                "Directive processing requires an AI agent occupant.",
                nameof(occupantConfiguration));
        }

        PositionEntityId = positionEntityId;
        Position = position;
        Occupant = occupant;
        OccupantConfiguration = occupantConfiguration;
        Authority = authority;
    }

    public PositionEntityId PositionEntityId { get; }

    public PositionRuntimeDescriptor Position { get; }

    public OccupantId Occupant { get; }

    public OccupantRuntimeConfiguration OccupantConfiguration { get; }

    public PositionAuthorityRuntimeConfiguration Authority { get; }
}

internal sealed record AiDirectiveProcessingLimits
{
    public AiDirectiveProcessingLimits(
        TimeSpan? timeout = null,
        int? maxOutputTokens = null,
        int? maxIterations = null,
        AiCostLimits? costLimits = null)
    {
        if (timeout is { } timeoutValue && timeoutValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Directive processing timeout must be greater than zero.");
        }

        if (maxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokens),
                maxOutputTokens,
                "Directive processing max output tokens must be greater than zero.");
        }

        if (maxIterations is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxIterations),
                maxIterations,
                "Directive processing max iterations must be greater than zero.");
        }

        Timeout = timeout;
        MaxOutputTokens = maxOutputTokens;
        MaxIterations = maxIterations;
        CostLimits = costLimits;
    }

    public TimeSpan? Timeout { get; }

    public int? MaxOutputTokens { get; }

    public int? MaxIterations { get; }

    public AiCostLimits? CostLimits { get; }

    public static AiDirectiveProcessingLimits From(AiPositionRuntimeConfiguration? configuration) =>
        new(
            configuration?.Timeout,
            configuration?.Parameters.MaxOutputTokens,
            configuration?.MaxIterations,
            configuration?.CostLimits);
}

internal sealed record AiDirectivePersistedContext
{
    public AiDirectivePersistedContext(
        PositionConfigurationStamp? lastConfigurationStamp = null,
        IEnumerable<PersistedTask>? openTasks = null,
        IReadOnlyDictionary<string, string>? shortMemory = null,
        IEnumerable<MessageId>? recentHistory = null)
    {
        LastConfigurationStamp = lastConfigurationStamp;
        OpenTasks = ToValidatedArray(openTasks, nameof(openTasks));
        ShortMemory = ToValidatedMemory(shortMemory, nameof(shortMemory));
        RecentHistory = ToValidatedArray(recentHistory, nameof(recentHistory));
    }

    public PositionConfigurationStamp? LastConfigurationStamp { get; }

    public ImmutableArray<PersistedTask> OpenTasks { get; }

    public ImmutableDictionary<string, string> ShortMemory { get; }

    public ImmutableArray<MessageId> RecentHistory { get; }

    public static AiDirectivePersistedContext From(PositionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new AiDirectivePersistedContext(
            state.LastConfigurationStamp,
            state.OpenTasks.Values.OrderBy(task => task.TaskId.Value),
            state.ShortMemory,
            state.RecentHistory);
    }

    private static ImmutableArray<T> ToValidatedArray<T>(
        IEnumerable<T>? source,
        string parameterName)
        where T : class
    {
        if (source is null)
        {
            return ImmutableArray<T>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<T>();
        foreach (var item in source)
        {
            if (item is null)
            {
                throw new ArgumentException("Collection cannot contain null items.", parameterName);
            }

            builder.Add(item);
        }

        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, string> ToValidatedMemory(
        IReadOnlyDictionary<string, string>? source,
        string parameterName)
    {
        if (source is null)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            RequireContent(key, parameterName);
            if (value is null)
            {
                throw new ArgumentException("Short-memory values cannot be null.", parameterName);
            }

            builder[key] = value;
        }

        return builder.ToImmutable();
    }

    private static string RequireContent(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Value cannot contain leading or trailing whitespace.",
                parameterName);
        }

        return value;
    }
}
