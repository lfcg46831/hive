namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// One entry of the prompt catalog (§4.8 <c>prompts[]</c>): an <see cref="Id"/> referenceable by
/// <c>occupant.identity_prompt_ref</c> and the versioned <see cref="Path"/> of the prompt file.
/// Uniqueness of <see cref="Id"/> and resolution of <see cref="Path"/> are validated later
/// (US-F0-05-T05/T06), not by this loaded model.
/// </summary>
public sealed record PromptConfiguration
{
    /// <summary>Creates a catalog entry binding <paramref name="id"/> to <paramref name="path"/>.</summary>
    public PromptConfiguration(string id, string path)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(path);

        Id = id;
        Path = path;
    }

    /// <summary>The catalog identifier referenced by <c>occupant.identity_prompt_ref</c>.</summary>
    public string Id { get; }

    /// <summary>The repository-relative path of the versioned prompt file.</summary>
    public string Path { get; }
}
