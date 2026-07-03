using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Actors.Positions;

internal enum AiDirectiveDecisionIntent
{
    Report = 1,
    Escalation = 2,
    Directive = 3,
}

internal static class AiDirectiveDecisionIntentContract
{
    public static AiDirectiveDecisionIntent RequireDefined(
        AiDirectiveDecisionIntent value,
        string parameterName)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "AI directive decision intent must be Report, Escalation, or Directive.");
        }

        return value;
    }

    public static string ToWireValue(AiDirectiveDecisionIntent value) =>
        RequireDefined(value, nameof(value)) switch
        {
            AiDirectiveDecisionIntent.Report => "Report",
            AiDirectiveDecisionIntent.Escalation => "Escalation",
            AiDirectiveDecisionIntent.Directive => "Directive",
            _ => throw new InvalidOperationException("Validated decision intent is not mapped."),
        };

    public static AiDirectiveDecisionIntent ParseWireValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            "Report" => AiDirectiveDecisionIntent.Report,
            "Escalation" => AiDirectiveDecisionIntent.Escalation,
            "Directive" => AiDirectiveDecisionIntent.Directive,
            _ => throw new ArgumentException(
                "AI directive decision intent must be Report, Escalation, or Directive.",
                nameof(value)),
        };
    }

    public static bool TryParseWireValue(string? value, out AiDirectiveDecisionIntent intent)
    {
        switch (value)
        {
            case "Report":
                intent = AiDirectiveDecisionIntent.Report;
                return true;
            case "Escalation":
                intent = AiDirectiveDecisionIntent.Escalation;
                return true;
            case "Directive":
                intent = AiDirectiveDecisionIntent.Directive;
                return true;
            default:
                intent = default;
                return false;
        }
    }
}

internal static class AiDirectiveDecisionSchema
{
    public const int SchemaVersion = 1;
    public const string SchemaVersionProperty = "schema_version";
    public const string IntentProperty = "intent";
    public const string ReportPayloadProperty = "report";
    public const string ReportKindField = "kind";
    public const string ReportBodyField = "body";
    public const string EscalationPayloadProperty = "escalation";
    public const string EscalationIssueField = "issue";
    public const string EscalationContextField = "context";
    public const string EscalationOptionsConsideredField = "options_considered";
    public const string DirectivePayloadProperty = "directive";
    public const string DirectiveTargetPositionIdField = "target_position_id";
    public const string DirectiveObjectiveField = "objective";
    public const string DirectiveContextField = "context";

    public static ImmutableArray<string> AllowedIntents { get; } =
    [
        AiDirectiveDecisionIntentContract.ToWireValue(AiDirectiveDecisionIntent.Report),
        AiDirectiveDecisionIntentContract.ToWireValue(AiDirectiveDecisionIntent.Escalation),
        AiDirectiveDecisionIntentContract.ToWireValue(AiDirectiveDecisionIntent.Directive),
    ];

    public static ImmutableArray<string> ReportRequiredFields { get; } =
    [
        ReportKindField,
        ReportBodyField,
    ];

    public static ImmutableArray<string> EscalationRequiredFields { get; } =
    [
        EscalationIssueField,
        EscalationContextField,
        EscalationOptionsConsideredField,
    ];

    public static ImmutableArray<string> DirectiveRequiredFields { get; } =
    [
        DirectiveTargetPositionIdField,
        DirectiveObjectiveField,
        DirectiveContextField,
    ];
}

internal abstract record AiDirectiveDecision
{
    protected AiDirectiveDecision(AiDirectiveDecisionIntent intent)
    {
        Intent = AiDirectiveDecisionIntentContract.RequireDefined(intent, nameof(intent));
    }

    public AiDirectiveDecisionIntent Intent { get; }
}

internal sealed record AiDirectiveReportDecision : AiDirectiveDecision
{
    public AiDirectiveReportDecision(ReportKind kind, string body)
        : base(AiDirectiveDecisionIntent.Report)
    {
        Kind = ReportKindContract.RequireDefined(kind, nameof(kind));
        Body = AiAgentGatewayText.Require(body, nameof(body));
    }

    public ReportKind Kind { get; }

    public string Body { get; }
}

internal sealed record AiDirectiveEscalationDecision : AiDirectiveDecision
{
    public AiDirectiveEscalationDecision(
        string issue,
        string context,
        IEnumerable<string> optionsConsidered)
        : base(AiDirectiveDecisionIntent.Escalation)
    {
        Issue = AiAgentGatewayText.Require(issue, nameof(issue));
        Context = AiAgentGatewayText.Require(context, nameof(context));
        OptionsConsidered = SnapshotOptions(optionsConsidered);
    }

    public string Issue { get; }

    public string Context { get; }

    public ImmutableArray<string> OptionsConsidered { get; }

    private static ImmutableArray<string> SnapshotOptions(IEnumerable<string> optionsConsidered)
    {
        ArgumentNullException.ThrowIfNull(optionsConsidered);

        return optionsConsidered
            .Select(option => AiAgentGatewayText.Require(option, nameof(optionsConsidered)))
            .ToImmutableArray();
    }
}

internal sealed record AiDirectiveChildDirectiveDecision : AiDirectiveDecision
{
    public AiDirectiveChildDirectiveDecision(
        PositionId targetPositionId,
        string objective,
        string context)
        : base(AiDirectiveDecisionIntent.Directive)
    {
        TargetPositionId = targetPositionId
            ?? throw new ArgumentNullException(nameof(targetPositionId));
        Objective = AiAgentGatewayText.Require(objective, nameof(objective));
        Context = AiAgentGatewayText.Require(context, nameof(context));
    }

    public PositionId TargetPositionId { get; }

    public string Objective { get; }

    public string Context { get; }
}
