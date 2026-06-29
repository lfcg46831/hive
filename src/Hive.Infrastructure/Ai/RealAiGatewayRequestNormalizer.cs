using System.Text.Json;
using Hive.Domain.Ai;
using Microsoft.Extensions.AI;

namespace Hive.Infrastructure.Ai;

internal sealed class RealAiGatewayRequestNormalizer
{
    public NormalizedAiGatewayProviderRequest Normalize(
        AiGatewayRequest request,
        RealAiGatewayProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);

        return new NormalizedAiGatewayProviderRequest(
            BuildMessages(request),
            BuildOptions(request, settings));
    }

    private static List<ChatMessage> BuildMessages(AiGatewayRequest request)
    {
        var messages = new List<ChatMessage>();

        if (request.SystemInstruction is { } systemInstruction)
        {
            messages.Add(new ChatMessage(ChatRole.System, systemInstruction));
        }

        foreach (var contextMessage in request.ContextMessages)
        {
            messages.Add(new ChatMessage(
                MapRole(contextMessage.Role),
                contextMessage.Content));
        }

        messages.Add(new ChatMessage(ChatRole.User, request.Content));

        return messages;
    }

    private static ChatOptions BuildOptions(
        AiGatewayRequest request,
        RealAiGatewayProviderSettings settings)
    {
        var parameters = MergeParameters(
            settings.DefaultParameters,
            request.ModelParameters);

        var options = new ChatOptions
        {
            ModelId = settings.DefaultProvider.ModelId,
            Temperature = parameters.Temperature is { } temperature
                ? (float)temperature
                : null,
            MaxOutputTokens = parameters.MaxOutputTokens,
        };

        if (request.Tools.Count > 0)
        {
            options.Tools = request.Tools.Select(MapTool).ToList();
        }

        return options;
    }

    private static AiModelParameters MergeParameters(
        AiModelParameters defaults,
        AiModelParameters request) =>
        new(
            request.Temperature ?? defaults.Temperature,
            request.MaxOutputTokens ?? defaults.MaxOutputTokens);

    private static AITool MapTool(AiToolDefinition tool) =>
        AIFunctionFactory.CreateDeclaration(
            tool.Name,
            tool.Description,
            ToJsonSchema(tool.ParametersSchema),
            returnJsonSchema: null);

    private static JsonElement ToJsonSchema(
        IReadOnlyDictionary<string, object?> schema)
    {
        if (schema.Count > 0)
        {
            return JsonSerializer.SerializeToElement(schema);
        }

        using var emptySchema = JsonDocument.Parse("{}");
        return emptySchema.RootElement.Clone();
    }

    private static ChatRole MapRole(AiGatewayMessageRole role) => role switch
    {
        AiGatewayMessageRole.System => ChatRole.System,
        AiGatewayMessageRole.User => ChatRole.User,
        AiGatewayMessageRole.Assistant => ChatRole.Assistant,
        AiGatewayMessageRole.Tool => ChatRole.Tool,
        _ => throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "AI gateway message role is undefined."),
    };
}
