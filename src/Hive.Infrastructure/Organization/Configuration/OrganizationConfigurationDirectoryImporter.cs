using Hive.Domain.Organization.Configuration;
using Hive.Domain.Organization.Configuration.Validation;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Infrastructure.Organization.Configuration;

public sealed class OrganizationConfigurationDirectoryImporter
{
    private const string OrganizationFileName = "organization.yaml";
    private readonly OrganizationConfigurationParser _parser;
    private readonly OrganizationConfigurationImporter _importer;

    public OrganizationConfigurationDirectoryImporter(
        OrganizationConfigurationParser parser,
        OrganizationConfigurationImporter importer)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
    }

    public async Task<IReadOnlyList<OrganizationImportResult>> ImportAsync(
        string organizationsRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationsRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(organizationsRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Organization configuration root '{root}' does not exist.");
        }

        var directories = Directory.EnumerateDirectories(root)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        if (directories.Length == 0)
        {
            throw new InvalidDataException(
                $"Organization configuration root '{root}' contains no organization directories.");
        }

        var results = new List<OrganizationImportResult>(directories.Length);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = Path.Combine(directory, OrganizationFileName);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    $"Organization directory '{directory}' does not contain '{OrganizationFileName}'.",
                    filePath);
            }

            var parseResult = _parser.ParseFile(filePath);
            if (!parseResult.IsSuccess)
            {
                throw new InvalidDataException(
                    $"Organization configuration '{filePath}' is invalid:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, parseResult.Errors));
            }

            var configuration = parseResult.Configuration!;
            var directoryName = Path.GetFileName(directory);
            if (!string.Equals(
                directoryName,
                configuration.Organization.Id.Value,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Organization directory '{directoryName}' must match organization id "
                    + $"'{configuration.Organization.Id.Value}'.");
            }

            var promptPathValidation = ValidatePromptPaths(configuration, directory);
            if (promptPathValidation.Count > 0)
            {
                throw new InvalidDataException(
                    $"Organization configuration '{filePath}' failed prompt path validation:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, promptPathValidation));
            }

            var result = await _importer
                .ImportAsync(configuration, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status == OrganizationImportStatus.Invalid)
            {
                throw new InvalidDataException(
                    $"Organization configuration '{filePath}' failed semantic validation:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, result.ValidationErrors));
            }

            results.Add(result);
        }

        return results.AsReadOnly();
    }

    private static IReadOnlyList<OrganizationConfigurationValidationError> ValidatePromptPaths(
        OrganizationConfiguration configuration,
        string organizationDirectory)
    {
        var errors = new List<OrganizationConfigurationValidationError>();
        var root = EnsureTrailingDirectorySeparator(Path.GetFullPath(organizationDirectory));

        for (var index = 0; index < configuration.Prompts.Count; index++)
        {
            var prompt = configuration.Prompts[index];
            var fieldPath = $"prompts[{index}].path";
            string resolvedPath;
            try
            {
                resolvedPath = Path.GetFullPath(prompt.Path, root);
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add(new OrganizationConfigurationValidationError(
                    "prompt-path-invalid",
                    fieldPath,
                    $"Prompt path '{prompt.Path}' cannot be resolved: {exception.Message}"));
                continue;
            }

            if (Path.IsPathRooted(prompt.Path))
            {
                errors.Add(new OrganizationConfigurationValidationError(
                    "prompt-path-not-relative",
                    fieldPath,
                    $"Prompt path '{prompt.Path}' resolves to '{resolvedPath}', but prompt paths must be relative to organization directory '{organizationDirectory}'."));
                continue;
            }

            if (!IsInsideDirectory(resolvedPath, root))
            {
                errors.Add(new OrganizationConfigurationValidationError(
                    "prompt-path-outside-organization-tree",
                    fieldPath,
                    $"Prompt path '{prompt.Path}' resolves to '{resolvedPath}', which is outside organization directory '{organizationDirectory}'."));
                continue;
            }

            if (!File.Exists(resolvedPath))
            {
                errors.Add(new OrganizationConfigurationValidationError(
                    "prompt-path-not-found",
                    fieldPath,
                    $"Prompt path '{prompt.Path}' resolves to '{resolvedPath}', but the file does not exist."));
                continue;
            }

            try
            {
                using var stream = File.Open(
                    resolvedPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception exception)
                when (exception is UnauthorizedAccessException or IOException)
            {
                errors.Add(new OrganizationConfigurationValidationError(
                    "prompt-path-not-readable",
                    fieldPath,
                    $"Prompt path '{prompt.Path}' resolves to '{resolvedPath}', but the file cannot be read: {exception.Message}"));
            }
        }

        return errors;
    }

    private static bool IsInsideDirectory(string candidatePath, string directoryRoot)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Path.GetFullPath(candidatePath).StartsWith(directoryRoot, comparison);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }
}
