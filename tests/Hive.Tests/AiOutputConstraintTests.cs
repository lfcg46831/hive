using System.Text.Json;
using Hive.Domain.Ai;

namespace Hive.Tests;

public sealed class AiOutputConstraintTests
{
    [Fact]
    public void Constraint_snapshots_schema_and_declares_only_explicit_fallbacks()
    {
        using var document = JsonDocument.Parse("""{"type":"object"}""");

        var constraint = new AiOutputConstraint(
            "decision_v1",
            1,
            document.RootElement,
            [
                AiOutputConstraintMode.Text,
                AiOutputConstraintMode.JsonObject,
                AiOutputConstraintMode.Text,
            ]);

        Assert.Equal("decision_v1", constraint.SchemaName);
        Assert.Equal(1, constraint.SchemaVersion);
        Assert.Equal("object", constraint.JsonSchema.GetProperty("type").GetString());
        Assert.Equal(
            [AiOutputConstraintMode.Text, AiOutputConstraintMode.JsonObject],
            constraint.AllowedFallbackModes);
        Assert.True(constraint.AllowsFallback(AiOutputConstraintMode.Text));
        Assert.False(constraint.AllowsFallback(AiOutputConstraintMode.JsonSchema));
    }

    [Fact]
    public void Constraint_rejects_non_object_schema_and_json_schema_fallback()
    {
        using var arrayDocument = JsonDocument.Parse("[]");
        using var objectDocument = JsonDocument.Parse("{}");

        Assert.Throws<ArgumentException>(() => new AiOutputConstraint(
            "decision_v1",
            1,
            arrayDocument.RootElement));
        Assert.Throws<ArgumentException>(() => new AiOutputConstraint(
            "decision_v1",
            1,
            objectDocument.RootElement,
            [AiOutputConstraintMode.JsonSchema]));
    }
}
