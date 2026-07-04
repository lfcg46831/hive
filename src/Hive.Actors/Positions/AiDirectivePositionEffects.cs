using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Actors.Positions;

internal sealed record AiDirectivePositionEffects
{
    private AiDirectivePositionEffects(
        string correlationId,
        ImmutableArray<PositionCommand> commands,
        AiDirectiveResultMessageFailure? failure)
    {
        if (commands.IsDefault)
        {
            throw new ArgumentException(
                "AI directive position effect commands cannot be default.",
                nameof(commands));
        }

        foreach (var command in commands)
        {
            if (command is null)
            {
                throw new ArgumentException(
                    "AI directive position effect commands cannot contain null entries.",
                    nameof(commands));
            }
        }

        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Commands = commands;
        Failure = failure;
    }

    public string CorrelationId { get; }

    public IReadOnlyList<PositionCommand> Commands { get; }

    public AiDirectiveResultMessageFailure? Failure { get; }

    public bool IsSuccess => Failure is null;

    public bool IsFailure => !IsSuccess;

    public static AiDirectivePositionEffects Success(
        string correlationId,
        IEnumerable<PositionCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        return new AiDirectivePositionEffects(
            correlationId,
            commands.ToImmutableArray(),
            failure: null);
    }

    public static AiDirectivePositionEffects Rejected(
        string correlationId,
        AiDirectiveResultMessageFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new AiDirectivePositionEffects(
            correlationId,
            ImmutableArray<PositionCommand>.Empty,
            failure);
    }
}

internal static class AiDirectivePositionEffectFactory
{
    public static AiDirectivePositionEffects Create(
        AiDirectiveExecutionContext context,
        AiDirectiveResultMessage result,
        Func<PositionTaskId>? newTaskId = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);

        if (!string.Equals(context.CorrelationId, result.CorrelationId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "AI directive result message correlation must match the execution context.",
                nameof(result));
        }

        if (result.IsFailure)
        {
            return AiDirectivePositionEffects.Rejected(
                context.CorrelationId,
                result.Failure!);
        }

        var taskIdFactory = newTaskId ?? PositionTaskId.New;
        var message = result.Message
            ?? throw new InvalidOperationException(
                "Successful AI directive result message must carry an organization message.");

        var commands = message switch
        {
            Report report => ReportEffects(context, report),
            Escalation escalation => EscalationEffects(context, escalation),
            OrgDirective directive => ChildDirectiveEffects(context, directive, taskIdFactory),
            _ => ImmutableArray<PositionCommand>.Empty,
        };

        return AiDirectivePositionEffects.Success(context.CorrelationId, commands);
    }

    private static ImmutableArray<PositionCommand> ReportEffects(
        AiDirectiveExecutionContext context,
        Report report)
    {
        var commands = ImmutableArray.CreateBuilder<PositionCommand>();
        commands.Add(new UpdateShortMemory(
            ResultMemoryKey(context),
            $"Report {ReportKindText(report.Kind)}: {report.Body}"));

        if (FindTaskCausedByDirective(context) is { } task)
        {
            commands.Add(report.Kind == ReportKind.Done
                ? new CompleteTask(task.TaskId, report.Body)
                : new UpdateTask(task.TaskId, report.Body));
        }

        return commands.ToImmutable();
    }

    private static ImmutableArray<PositionCommand> EscalationEffects(
        AiDirectiveExecutionContext context,
        Escalation escalation)
    {
        var commands = ImmutableArray.CreateBuilder<PositionCommand>();
        var note = $"Escalation: {escalation.Issue}. {escalation.Context}";
        commands.Add(new UpdateShortMemory(ResultMemoryKey(context), note));

        if (FindTaskCausedByDirective(context) is { } task)
        {
            commands.Add(new UpdateTask(task.TaskId, note, Priority.Critical));
        }

        return commands.ToImmutable();
    }

    private static ImmutableArray<PositionCommand> ChildDirectiveEffects(
        AiDirectiveExecutionContext context,
        OrgDirective directive,
        Func<PositionTaskId> newTaskId)
    {
        var commands = ImmutableArray.CreateBuilder<PositionCommand>();
        var target = TargetText(directive.To);
        commands.Add(new UpdateShortMemory(
            ResultMemoryKey(context),
            $"Delegated directive to {target}: {directive.Objective}"));
        commands.Add(new OpenTask(
            newTaskId(),
            context.Directive.ThreadId,
            $"Follow delegated directive to {target}",
            context.Directive.Priority,
            context.Directive.Deadline,
            context.Directive.MessageId));

        return commands.ToImmutable();
    }

    private static PersistedTask? FindTaskCausedByDirective(AiDirectiveExecutionContext context)
    {
        var matches = context.OpenTasks
            .Where(task => task.CausedBy == context.Directive.MessageId)
            .Take(2)
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    private static string ResultMemoryKey(AiDirectiveExecutionContext context) =>
        $"directive:{context.Directive.DirectiveId.Value:N}:result";

    private static string ReportKindText(ReportKind kind) =>
        ReportKindContract.RequireDefined(kind, nameof(kind)) switch
        {
            ReportKind.Progress => "Progress",
            ReportKind.Done => "Done",
            _ => throw new InvalidOperationException("Validated report kind is not mapped."),
        };

    private static string TargetText(EndpointRef endpoint) =>
        endpoint switch
        {
            PositionEndpointRef position => position.PositionId.Value,
            OrganizationOwnerEndpointRef => "organization-owner",
            _ => endpoint.GetType().Name,
        };
}

internal sealed record GetAiDirectivePositionEffects
{
    public GetAiDirectivePositionEffects(string correlationId)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

internal sealed record AiDirectivePositionEffectsQueryResult
{
    private AiDirectivePositionEffectsQueryResult(
        string correlationId,
        AiDirectivePositionEffects? effects)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Effects = effects;
    }

    public string CorrelationId { get; }

    public AiDirectivePositionEffects? Effects { get; }

    public bool Found => Effects is not null;

    public static AiDirectivePositionEffectsQueryResult FoundEffects(
        AiDirectivePositionEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);

        return new AiDirectivePositionEffectsQueryResult(
            effects.CorrelationId,
            effects);
    }

    public static AiDirectivePositionEffectsQueryResult Missing(string correlationId) =>
        new(correlationId, effects: null);
}
