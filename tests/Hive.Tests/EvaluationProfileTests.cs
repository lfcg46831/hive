using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class EvaluationProfileTests
{
    private static readonly OrganizationId Acme = OrganizationId.From("acme-delivery");
    private static readonly PositionId BugTriage = PositionId.From("bug-triage");

    [Fact]
    public void Evaluation_is_disabled_by_default()
    {
        using var services = Services(new Dictionary<string, string?>());

        var provider = services.GetRequiredService<IEvaluationInstructionProvider>();

        Assert.Null(provider.Resolve(Acme, BugTriage));
    }

    [Fact]
    public void Enabled_profile_generates_instruction_only_for_its_exact_scope()
    {
        using var services = Services(ProfileConfiguration(RubricFile));
        var provider = services.GetRequiredService<IEvaluationInstructionProvider>();

        var instruction = provider.Resolve(Acme, BugTriage);

        Assert.NotNull(instruction);
        Assert.Equal(1, instruction.RubricVersion);
        Assert.Contains(
            "hive-evaluation-v1:{\"dimensions\":",
            instruction.Content,
            StringComparison.Ordinal);
        Assert.Contains("\"severity\" (single-label): exactly one label", instruction.Content, StringComparison.Ordinal);
        Assert.Contains("\"missing-information\" (label-set): zero or more labels", instruction.Content, StringComparison.Ordinal);
        Assert.Contains("\"critical\"", instruction.Content, StringComparison.Ordinal);
        Assert.Contains("\"correlation-metadata\"", instruction.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"decision\"", instruction.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("ordinal-distance", instruction.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("set-f1", instruction.Content, StringComparison.Ordinal);
        Assert.Null(provider.Resolve(OrganizationId.From("other-organization"), BugTriage));
        Assert.Null(provider.Resolve(Acme, PositionId.From("other-position")));
    }

    [Fact]
    public void Disabled_profile_does_not_require_or_expose_scope_and_rubric()
    {
        using var services = Services(new Dictionary<string, string?>
        {
            ["Hive:Evaluation:Profiles:draft:Enabled"] = "false",
        });

        var provider = services.GetRequiredService<IEvaluationInstructionProvider>();

        Assert.Null(provider.Resolve(Acme, BugTriage));
    }

    [Theory]
    [InlineData("\"source\": \"evaluation-envelope\"", "\"source\": \"unknown-source\"", "unknown source")]
    [InlineData("\"value_kind\": \"label-set\"", "\"value_kind\": \"ranked-list\"", "unknown value kind")]
    [InlineData("\"value_kind\": \"label-set\"", "\"value_kind\": \"single-label\"", "incompatible value kind")]
    [InlineData("\"scorer\": \"ordinal-distance\"", "\"scorer\": \"role-specific-score\"", "unknown scorer")]
    [InlineData("\"scorer_version\": 1", "\"scorer_version\": 2", "unknown scorer")]
    [InlineData("\"report\": \"report\"", "\"report\": \"unknown-result\"", "source_mapping")]
    [InlineData("\"id\": \"missing-information\"", "\"id\": \"severity\"", "duplicated")]
    [InlineData("        \"critical\"", "        \"high\"", "unique label vocabulary")]
    [InlineData("\"weight\": 0.30", "\"weight\": 0.31", "weights")]
    public void Invalid_enabled_rubric_fails_fast(
        string original,
        string replacement,
        string expectedMessage)
    {
        using var rubric = TemporaryRubric.WithReplacement(original, replacement);
        using var services = Services(ProfileConfiguration(rubric.Path));

        var exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IEvaluationInstructionProvider>());

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_or_version_incompatible_rubric_fails_fast()
    {
        var missingConfiguration = ProfileConfiguration(
            Path.Combine(Path.GetTempPath(), $"missing-rubric-{Guid.NewGuid():N}.json"));
        using var missingServices = Services(missingConfiguration);
        var missing = Assert.Throws<OptionsValidationException>(
            () => missingServices.GetRequiredService<IEvaluationInstructionProvider>());

        var versionConfiguration = ProfileConfiguration(RubricFile);
        versionConfiguration["Hive:Evaluation:Profiles:bug-triage:RubricVersion"] = "2";
        using var versionServices = Services(versionConfiguration);
        var incompatible = Assert.Throws<OptionsValidationException>(
            () => versionServices.GetRequiredService<IEvaluationInstructionProvider>());

        Assert.Contains("invalid", missing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not match", incompatible.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_enabled_scope_fails_fast()
    {
        var configuration = ProfileConfiguration(RubricFile);
        foreach (var (key, value) in ProfileConfiguration(RubricFile, "duplicate"))
        {
            configuration[key] = value;
        }

        using var services = Services(configuration);

        var exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IEvaluationInstructionProvider>());

        Assert.Contains("more than one enabled profile", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider Services(IReadOnlyDictionary<string, string?> configuration)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddHiveBootstrap();
        return builder.Services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> ProfileConfiguration(
        string rubricPath,
        string profileName = "bug-triage") =>
        new(StringComparer.Ordinal)
        {
            [$"Hive:Evaluation:Profiles:{profileName}:Enabled"] = "true",
            [$"Hive:Evaluation:Profiles:{profileName}:OrganizationId"] = Acme.Value,
            [$"Hive:Evaluation:Profiles:{profileName}:PositionId"] = BugTriage.Value,
            [$"Hive:Evaluation:Profiles:{profileName}:RubricPath"] = rubricPath,
            [$"Hive:Evaluation:Profiles:{profileName}:RubricVersion"] = "1",
        };

    private sealed class TemporaryRubric : IDisposable
    {
        private TemporaryRubric(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryRubric WithReplacement(string original, string replacement)
        {
            var content = File.ReadAllText(RubricFile);
            var changed = content.Replace(original, replacement, StringComparison.Ordinal);
            if (string.Equals(content, changed, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Rubric mutation source '{original}' was not found.");
            }

            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"hive-evaluation-rubric-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, changed);
            return new TemporaryRubric(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    private static string RubricFile => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "bug-triage-rubric.v1.json");

    private static string RepositoryRoot
    {
        get
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
}
