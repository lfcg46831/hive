using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Governance;

namespace Hive.Actors.Positions;

internal sealed record AiToolCallActingUnderBinding
{
    public AiToolCallActingUnderBinding(
        AiToolCall toolCall,
        ActingUnderDeclaration declaration)
    {
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
        Declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
    }

    public AiToolCall ToolCall { get; }

    public ActingUnderDeclaration Declaration { get; }
}

internal static class AiToolCallActingUnderBinder
{
    public const string PropertyName = "acting_under";

    public static AiToolCallActingUnderBinding Bind(
        AiToolCall toolCall,
        IEnumerable<AuthorityKey> canDecide)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(canDecide);

        var fieldPresent = toolCall.Arguments.TryGetValue(PropertyName, out var rawValue);
        var declaration = ActingUnderDeclaration.Resolve(
            fieldPresent,
            ReadString(rawValue),
            canDecide);
        var functionalArguments = toolCall.Arguments
            .Where(argument => !string.Equals(
                argument.Key,
                PropertyName,
                StringComparison.Ordinal))
            .ToDictionary(
                argument => argument.Key,
                argument => argument.Value,
                StringComparer.Ordinal);

        return new AiToolCallActingUnderBinding(
            new AiToolCall(toolCall.Id, toolCall.Name, functionalArguments),
            declaration);
    }

    private static string? ReadString(object? value) =>
        value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
}
