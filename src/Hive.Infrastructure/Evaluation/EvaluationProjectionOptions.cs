namespace Hive.Infrastructure.Evaluation;

public sealed class EvaluationProjectionOptions
{
    public const string SectionName = "Hive:EvaluationProjection";

    public bool Enabled { get; set; }

    public string RubricPath { get; set; } = Path.Combine(
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "bug-triage-rubric.v1.json");

    public string ResolveRubricPath(string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        if (string.IsNullOrWhiteSpace(RubricPath))
        {
            throw new InvalidDataException(
                "Hive:EvaluationProjection:RubricPath is required when evaluation projection is enabled.");
        }

        return Path.IsPathRooted(RubricPath)
            ? Path.GetFullPath(RubricPath)
            : Path.GetFullPath(RubricPath, contentRootPath);
    }
}
