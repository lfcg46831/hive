namespace Hive.Domain.Positions;

/// <summary>
/// Resolved identity prompt materialized from the organization prompt catalog for one AI occupant.
/// </summary>
public sealed record IdentityPromptRuntimeConfiguration
{
    public IdentityPromptRuntimeConfiguration(string id, string path, string content)
    {
        Id = CommandText.RequireContent(id, nameof(id));
        Path = CommandText.RequireContent(path, nameof(path));
        Content = RequirePromptContent(content, nameof(content));
    }

    /// <summary>The prompt catalog id referenced by the occupant.</summary>
    public string Id { get; }

    /// <summary>The catalog path for the versioned prompt file, relative to the organization directory.</summary>
    public string Path { get; }

    /// <summary>The prompt file content. Formatting is preserved except that blank-only content is rejected.</summary>
    public string Content { get; }

    private static string RequirePromptContent(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identity prompt content cannot be empty or whitespace.", parameterName);
        }

        return value;
    }
}
