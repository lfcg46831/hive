using System.Reflection;
using System.Xml.Linq;
using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Application.Directives;
using Hive.Domain.Auditing;
using Hive.Domain.Directives;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class ApplicationBoundaryTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Akka",
        "Hive.Actors",
        "Hive.Api",
        "Hive.DemoClient",
        "Hive.Evaluation.Tooling",
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
    public void Evaluation_tooling_depends_only_on_the_public_contracts_project()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Hive.Evaluation.Tooling",
            "Hive.Evaluation.Tooling.csproj"));
        var references = project
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value.Replace('\\', '/'))
            .OfType<string>()
            .ToArray();

        Assert.Equal(["../Hive.Contracts/Hive.Contracts.csproj"], references);
        Assert.Empty(project.Descendants("PackageReference"));
    }

    [Fact]
    public void Normal_runtime_projects_have_no_evaluation_tooling_runner_scorer_or_rubric_dependencies()
    {
        string[] runtimeProjects =
        [
            "Hive.Domain",
            "Hive.Application",
            "Hive.Actors",
            "Hive.Infrastructure",
            "Hive.Api",
            "Hive.Worker",
        ];
        string[] forbiddenSymbols =
        [
            "Hive.Evaluation.Tooling",
            "IEvaluationInstructionProvider",
            "IEvaluationResultProjector",
            "EvaluationRubric",
            "EvaluationRunner",
            "EvaluationScorer",
        ];
        var violations = runtimeProjects
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "src", project),
                "*.*",
                SearchOption.AllDirectories)
                .Where(path =>
                    path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .SelectMany(path => forbiddenSymbols
                    .Where(symbol => File.ReadAllText(path).Contains(
                        symbol,
                        StringComparison.Ordinal))
                    .Select(symbol => $"{project}: {symbol}")))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Evaluation tooling leaked into the normal runtime:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void Demo_client_no_longer_hosts_evaluation_tooling_or_database_access()
    {
        var formerEvaluationDirectory = Path.Combine(
            RepositoryRoot,
            "src",
            "Hive.DemoClient",
            "Evaluation");
        Assert.False(
            Directory.Exists(formerEvaluationDirectory) &&
            Directory.EnumerateFiles(
                formerEvaluationDirectory,
                "*.cs",
                SearchOption.AllDirectories).Any());
        var project = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "Hive.DemoClient",
            "Hive.DemoClient.csproj"));

        Assert.DoesNotContain("Npgsql", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Hive.Evaluation.Tooling", project, StringComparison.Ordinal);
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

    [Fact]
    public void Ai_actor_delegates_execution_without_owning_the_loop_or_outcome_composition()
    {
        Assert.Contains(
            typeof(IDirectiveExecutionCoordinator),
            typeof(AiDirectiveExecutionCoordinator).GetInterfaces());

        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "Hive.Actors",
            "Positions",
            "PositionOccupantFactory.cs"));
        var actorStart = source.IndexOf(
            "internal sealed class AiAgentActor",
            StringComparison.Ordinal);
        var actorEnd = source.IndexOf(
            "internal sealed class HumanProxyActor",
            actorStart,
            StringComparison.Ordinal);
        Assert.True(actorStart >= 0 && actorEnd > actorStart);

        var actorSource = source[actorStart..actorEnd];
        Assert.DoesNotContain("while (true)", actorSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AiDirectiveIterationState.Start",
            actorSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AiDirectivePositionEffectFactory.Create",
            actorSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AiDirectivePrompt.CreateInitialRequest",
            actorSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AiDirectiveDecisionInterpreter.Interpret",
            actorSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AiDirectiveIterationExecutor",
            actorSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ExecutionBudget.",
            actorSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AiDirectiveAuditSnapshotFactory",
            actorSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".ResolveAsync(",
            actorSource,
            StringComparison.Ordinal);
        Assert.Contains(
            ".ExecuteDetailedAsync(request",
            actorSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_has_no_actor_journal_event_stream_or_projection_dispatch_dependency()
    {
        var coordinatorType = typeof(AiDirectiveExecutionCoordinator);
        Assert.False(typeof(ActorBase).IsAssignableFrom(coordinatorType));

        var dependencyTypes = coordinatorType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .Concat(coordinatorType
                .GetConstructors()
                .SelectMany(constructor => constructor
                    .GetParameters()
                    .Select(parameter => parameter.ParameterType)))
            .ToArray();
        var forbiddenTypes = new[]
        {
            typeof(IActorRef),
            typeof(IJourneyAuditLog),
        };

        var violations = dependencyTypes
            .Where(candidate => forbiddenTypes.Any(forbidden =>
                forbidden.IsAssignableFrom(candidate)))
            .Select(type => type.FullName)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Forbidden coordinator dispatch dependencies:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void Ai_actor_uses_one_neutral_completion_path_for_execution_and_recovery()
    {
        var completion = Assert.Single(typeof(AiAgentActor)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == "ReturnCompletion"));

        Assert.Equal(
            [typeof(IActorRef), typeof(DirectiveExecutionResult)],
            completion.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void Application_execution_contracts_do_not_extend_persisted_or_wire_protocols()
    {
        var applicationTypes = typeof(IDirectiveExecutionCoordinator)
            .Assembly
            .GetTypes();
        var persistedProtocolRoots = new[]
        {
            typeof(OrgMessage),
            typeof(PositionCommand),
            typeof(PositionEvent),
        };

        var violations = applicationTypes
            .Where(type => persistedProtocolRoots.Any(root =>
                root.IsAssignableFrom(type)))
            .Select(type => type.FullName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Hive.Application types added to persisted or wire protocols:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void Checkpoint_plan_contracts_remain_position_local_data_not_messages_or_commands()
    {
        var checkpointContractTypes = new[]
        {
            typeof(DirectiveCheckpoint),
            typeof(DirectiveCheckpointPlan),
            typeof(DirectiveCheckpointSubtask),
            typeof(CompletedDirectiveCheckpointSubtask),
            typeof(DirectiveCheckpointCorrelation),
        };
        var protocolRoots = new[]
        {
            typeof(OrgMessage),
            typeof(PositionCommand),
            typeof(PositionEvent),
        };

        var violations = checkpointContractTypes
            .Where(type => protocolRoots.Any(root => root.IsAssignableFrom(type)))
            .Select(type => type.FullName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Checkpoint plan contracts leaked into organizational or actor protocols:\n" +
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
