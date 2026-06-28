namespace Hive.Domain.Ai;

public sealed record AiToolDefinition
{
    public AiToolDefinition(
        string name,
        string description,
        IReadOnlyDictionary<string, object?>? parametersSchema = null)
    {
        Name = AiContractGuards.RequireText(name, nameof(name));
        Description = AiContractGuards.RequireText(description, nameof(description));
        ParametersSchema = AiContractGuards.SnapshotData(
            parametersSchema,
            nameof(parametersSchema));
    }

    public string Name { get; }

    public string Description { get; }

    public IReadOnlyDictionary<string, object?> ParametersSchema { get; }
}
