using System.Reflection;
using System.Xml.Linq;

namespace Hive.Tests;

public sealed class DomainIsolationTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Akka",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.AI",
        "Microsoft.Extensions.Hosting",
        "Anthropic",
        "Azure.AI",
        "OpenAI",
    ];

    [Fact]
    public void Domain_project_declares_no_external_or_project_dependencies()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Hive.Domain",
            "Hive.Domain.csproj"));

        var dependencyElements = project
            .Descendants()
            .Where(element => element.Name.LocalName is
                "ProjectReference" or "PackageReference" or "FrameworkReference" or "Reference")
            .Select(element => element.ToString())
            .ToArray();

        Assert.Empty(dependencyElements);
    }

    [Fact]
    public void Domain_assembly_references_no_runtime_framework_or_host_projects()
    {
        var assembly = Assembly.LoadFrom(Path.Combine(
            AppContext.BaseDirectory,
            "Hive.Domain.dll"));
        var references = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .ToArray();

        Assert.DoesNotContain(
            references,
            reference => ForbiddenAssemblyPrefixes.Any(prefix =>
                reference.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.DoesNotContain(
            references,
            reference => reference.StartsWith("Hive.", StringComparison.Ordinal));
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
