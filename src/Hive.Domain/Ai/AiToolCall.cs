namespace Hive.Domain.Ai;

public sealed record AiToolCall
{
    public AiToolCall(
        string id,
        string name,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        Id = AiContractGuards.RequireText(id, nameof(id));
        Name = AiContractGuards.RequireText(name, nameof(name));
        Arguments = AiContractGuards.SnapshotData(arguments, nameof(arguments));
    }

    public string Id { get; }

    public string Name { get; }

    public IReadOnlyDictionary<string, object?> Arguments { get; }
}
