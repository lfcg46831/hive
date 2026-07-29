using System.Reflection;
using System.Xml.Linq;
using Hive.Application.Directives;

namespace Hive.Tests;

public sealed class ApplicationBoundaryTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Akka",
        "Hive.Actors",
        "Hive.Api",
        "Hive.DemoClient",
        "Hive.Infrastructure",
        "Hive.Worker",
        "Microsoft.Extensions.AI",
        "Microsoft.Extensions.Hosting",
        "OpenAI",
    ];

    [Fact]
    public void Application_project_references_only_Domain_and_has_no_packages()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Hive.Application",
            "Hive.Application.csproj"));

        var projectReferences = project
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value.Replace('\\', '/'))
            .OfType<string>()
            .ToArray();

        Assert.Equal(["../Hive.Domain/Hive.Domain.csproj"], projectReferences);
        Assert.Empty(project.Descendants("PackageReference"));
    }

    [Fact]
    public void Application_assembly_has_no_actor_host_infrastructure_evaluation_runner_or_provider_dependencies()
    {
        var references = typeof(IDirectiveExecutionCoordinator)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        var violations = references
            .Where(reference => ForbiddenAssemblyPrefixes.Any(prefix =>
                reference.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Forbidden Hive.Application assembly references:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void Budget_is_explicit_on_inference_tool_and_verifier_ports()
    {
        var portTypes = new[]
        {
            typeof(IDirectiveInferencePort),
            typeof(IDirectiveToolPort),
            typeof(IDirectiveOutcomeVerifierPort),
        };

        foreach (var portType in portTypes)
        {
            var operation = Assert.Single(portType.GetMethods());
            Assert.Contains(
                operation.GetParameters(),
                parameter => parameter.ParameterType == typeof(ExecutionBudget));
        }
    }

    [Fact]
    public void Coordinator_surface_is_actor_and_provider_neutral()
    {
        var contractTypes = typeof(IDirectiveExecutionCoordinator)
            .Assembly
            .GetExportedTypes();
        var violations = contractTypes
            .SelectMany(PublicSurfaceTypes)
            .Where(type => type.Namespace is { } typeNamespace &&
                ForbiddenAssemblyPrefixes.Any(prefix =>
                    typeNamespace.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(type => type.FullName)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Forbidden public Hive.Application contract types:\n" +
            string.Join("\n", violations));
    }

    private static IEnumerable<Type> PublicSurfaceTypes(Type type)
    {
        yield return type;

        foreach (var constructor in type.GetConstructors())
        {
            foreach (var parameter in constructor.GetParameters())
            {
                foreach (var candidate in Flatten(parameter.ParameterType))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var method in type.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.DeclaredOnly))
        {
            foreach (var candidate in Flatten(method.ReturnType))
            {
                yield return candidate;
            }

            foreach (var parameter in method.GetParameters())
            {
                foreach (var candidate in Flatten(parameter.ParameterType))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var property in type.GetProperties(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.DeclaredOnly))
        {
            foreach (var candidate in Flatten(property.PropertyType))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        var candidate = type.IsByRef || type.IsArray
            ? type.GetElementType()!
            : type;
        yield return candidate;
        foreach (var argument in candidate.GetGenericArguments())
        {
            foreach (var nested in Flatten(argument))
            {
                yield return nested;
            }
        }
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
