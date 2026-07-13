using System.Collections.Immutable;
using System.Text.Json;

namespace Hive.Domain.Ai;

public sealed record AiOutputConstraint
{
    public AiOutputConstraint(
        string schemaName,
        int schemaVersion,
        JsonElement jsonSchema,
        IEnumerable<AiOutputConstraintMode>? allowedFallbackModes = null)
    {
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "AI output constraint schema version must be greater than zero.");
        }

        if (jsonSchema.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException(
                "AI output constraint JSON schema must be an object.",
                nameof(jsonSchema));
        }

        SchemaName = AiContractGuards.RequireText(schemaName, nameof(schemaName));
        SchemaVersion = schemaVersion;
        JsonSchema = jsonSchema.Clone();
        AllowedFallbackModes = SnapshotFallbackModes(allowedFallbackModes);
    }

    public string SchemaName { get; }

    public int SchemaVersion { get; }

    public JsonElement JsonSchema { get; }

    public ImmutableArray<AiOutputConstraintMode> AllowedFallbackModes { get; }

    public bool AllowsFallback(AiOutputConstraintMode mode) =>
        AllowedFallbackModes.Contains(
            AiOutputConstraintModeContract.RequireDefined(mode, nameof(mode)));

    private static ImmutableArray<AiOutputConstraintMode> SnapshotFallbackModes(
        IEnumerable<AiOutputConstraintMode>? modes)
    {
        if (modes is null)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<AiOutputConstraintMode>();
        foreach (var mode in modes)
        {
            var defined = AiOutputConstraintModeContract.RequireDefined(
                mode,
                nameof(modes));
            if (defined is AiOutputConstraintMode.JsonSchema)
            {
                throw new ArgumentException(
                    "JSON schema is the requested output mode and cannot be declared as a fallback.",
                    nameof(modes));
            }

            if (!builder.Contains(defined))
            {
                builder.Add(defined);
            }
        }

        return builder.ToImmutable();
    }
}
