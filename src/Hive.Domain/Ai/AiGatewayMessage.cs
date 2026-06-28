namespace Hive.Domain.Ai;

public enum AiGatewayMessageRole
{
    System = 1,
    User = 2,
    Assistant = 3,
    Tool = 4,
}

public sealed record AiGatewayMessage
{
    public AiGatewayMessage(AiGatewayMessageRole role, string content)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "AI gateway message role is undefined.");
        }

        Role = role;
        Content = AiContractGuards.RequireText(content, nameof(content));
    }

    public AiGatewayMessageRole Role { get; }

    public string Content { get; }
}
