namespace Hive.Infrastructure.Evaluation;

public sealed class EvaluationOptions
{
    public const string SectionName = "Hive:Evaluation";

    public Dictionary<string, EvaluationProfileOptions> Profiles { get; set; } =
        new(StringComparer.Ordinal);
}

public sealed class EvaluationProfileOptions
{
    public bool Enabled { get; set; }

    public string OrganizationId { get; set; } = string.Empty;

    public string PositionId { get; set; } = string.Empty;

    public string RubricPath { get; set; } = string.Empty;

    public int RubricVersion { get; set; }

    internal string ResolveRubricPath(string contentRootPath, string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        if (string.IsNullOrWhiteSpace(RubricPath))
        {
            throw new InvalidDataException(
                $"Hive:Evaluation:Profiles:{profileName}:RubricPath is required when the profile is enabled.");
        }

        return Path.IsPathRooted(RubricPath)
            ? Path.GetFullPath(RubricPath)
            : Path.GetFullPath(RubricPath, contentRootPath);
    }
}
