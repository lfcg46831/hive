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
}
