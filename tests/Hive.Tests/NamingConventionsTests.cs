using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hive.Tests;

/// <summary>
/// Enforces the namespace, folder, naming and boundary conventions promoted to a
/// contract in §5.11 of the bible: <c>Hive</c> is the stable technical codename,
/// every project has a single root namespace equal to its name, sub-namespaces
/// mirror the folder structure, and project references only flow along the
/// allowed (inward, towards the domain) graph.
/// </summary>
public sealed class NamingConventionsTests
{
    private static readonly Regex NamespaceDeclaration = new(
        @"^\s*namespace\s+([A-Za-z0-9_.]+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Project folder (relative to the repository root) → root namespace.</summary>
    private static readonly (string RelativeDirectory, string RootNamespace)[] Projects =
    [
        ("src/Hive.Domain", "Hive.Domain"),
        ("src/Hive.Actors", "Hive.Actors"),
        ("src/Hive.Infrastructure", "Hive.Infrastructure"),
        ("src/Hive.Api", "Hive.Api"),
        ("src/Hive.Worker", "Hive.Worker"),
        ("src/Hive.DemoClient", "Hive.DemoClient"),
        ("tests/Hive.Tests", "Hive.Tests"),
        ("tests/Hive.DemoClient.Tests", "Hive.DemoClient.Tests"),
    ];

    /// <summary>
    /// The boundary contract: each project may only reference the projects listed here.
    /// Any edge outside this set is a boundary violation.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedProjectReferences = new()
    {
        ["Hive.Domain"] = [],
        ["Hive.Infrastructure"] = ["Hive.Domain"],
        ["Hive.Actors"] = ["Hive.Domain", "Hive.Infrastructure"],
        ["Hive.Api"] = ["Hive.Domain", "Hive.Actors", "Hive.Infrastructure"],
        ["Hive.Worker"] = ["Hive.Domain", "Hive.Actors", "Hive.Infrastructure"],
        ["Hive.DemoClient"] = [],
        ["Hive.Tests"] = ["Hive.Domain", "Hive.Actors", "Hive.Infrastructure", "Hive.Api", "Hive.Worker"],
        ["Hive.DemoClient.Tests"] = ["Hive.Domain", "Hive.Api", "Hive.DemoClient"],
    };

    public static TheoryData<string> ProjectRootNamespaces
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var (_, rootNamespace) in Projects)
            {
                data.Add(rootNamespace);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ProjectRootNamespaces))]
    public void Every_project_uses_the_Hive_codename(string rootNamespace)
    {
        Assert.StartsWith("Hive.", rootNamespace, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_namespaces_mirror_project_and_folder_structure()
    {
        var violations = new List<string>();

        foreach (var (relativeDirectory, rootNamespace) in Projects)
        {
            var projectDirectory = Path.Combine(RepositoryRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

            foreach (var file in EnumerateSourceFiles(projectDirectory))
            {
                var match = NamespaceDeclaration.Match(File.ReadAllText(file));
                if (!match.Success)
                {
                    // Files without a namespace declaration (e.g. pure top-level statements) are skipped.
                    continue;
                }

                var declaredNamespace = match.Groups[1].Value;
                var expectedNamespace = ExpectedNamespace(projectDirectory, file, rootNamespace);

                if (!string.Equals(declaredNamespace, expectedNamespace, StringComparison.Ordinal))
                {
                    var relativeFile = Path.GetRelativePath(RepositoryRoot, file);
                    violations.Add($"{relativeFile}: declares '{declaredNamespace}', expected '{expectedNamespace}'.");
                }
            }
        }

        Assert.True(violations.Count == 0, "Namespace convention violations:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Project_references_stay_within_the_allowed_boundary_graph()
    {
        var violations = new List<string>();

        foreach (var (relativeDirectory, projectName) in Projects)
        {
            var projectPath = Path.Combine(
                RepositoryRoot,
                relativeDirectory.Replace('/', Path.DirectorySeparatorChar),
                projectName + ".csproj");

            var allowed = AllowedProjectReferences[projectName];

            var references = XDocument.Load(projectPath)
                .Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")?.Value)
                .OfType<string>()
                .Select(value => Path.GetFileNameWithoutExtension(value.Replace('\\', '/')))
                .ToArray();

            foreach (var reference in references)
            {
                if (!allowed.Contains(reference, StringComparer.Ordinal))
                {
                    violations.Add($"{projectName} references '{reference}', which is outside its allowed boundary.");
                }
            }
        }

        Assert.True(violations.Count == 0, "Boundary violations:\n" + string.Join("\n", violations));
    }

    private static string ExpectedNamespace(string projectDirectory, string file, string rootNamespace)
    {
        var fileDirectory = Path.GetDirectoryName(file)!;
        var relative = Path.GetRelativePath(projectDirectory, fileDirectory);

        if (relative is "." or "")
        {
            return rootNamespace;
        }

        var suffix = relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(segment => segment.Length > 0);

        return rootNamespace + "." + string.Join('.', suffix);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string projectDirectory)
    {
        return Directory
            .EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var normalized = file.Replace('\\', '/');
                return !normalized.Contains("/bin/", StringComparison.Ordinal)
                    && !normalized.Contains("/obj/", StringComparison.Ordinal);
            });
    }

    private static string RepositoryRoot => FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the Hive repository root.");
    }
}
