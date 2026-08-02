using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Hive.Contracts.Audit;

namespace Hive.Tests;

public sealed class AuditExportContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    [Fact]
    public void V1_contract_matches_the_tracked_wire_fixture_and_round_trips()
    {
        var page = ExamplePage();
        var actual = JsonSerializer.Serialize(page, JsonOptions)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var expected = File.ReadAllText(FixturePath)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n');

        Assert.Equal(expected, actual);

        var roundTripped = JsonSerializer.Deserialize<DirectiveAuditExportPage>(
            expected,
            JsonOptions);
        Assert.NotNull(roundTripped);
        Assert.Equal(AuditExportContract.Name, roundTripped.ContractName);
        Assert.Equal(AuditExportContract.Version, roundTripped.ContractVersion);
        Assert.True(roundTripped.IsTerminal);
        Assert.Equal(2, roundTripped.Events.Count);
        Assert.NotNull(roundTripped.Result);
        Assert.Equal(416, roundTripped.Result.ContentLengthBytes);
    }

    [Fact]
    public void Page_rejects_unsupported_versions_unbounded_pages_and_invalid_cursors()
    {
        var item = ExampleEvent(sequence: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new DirectiveAuditExportPage(
            AuditExportContract.Name,
            contractVersion: 2,
            "acme-delivery",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            afterSequence: 0,
            nextAfterSequence: 1,
            isTerminal: false,
            [item]));

        Assert.Throws<ArgumentException>(() => new DirectiveAuditExportPage(
            AuditExportContract.Name,
            AuditExportContract.Version,
            "acme-delivery",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            afterSequence: 1,
            nextAfterSequence: 1,
            isTerminal: false,
            [item]));

        var tooMany = Enumerable.Range(
                1,
                AuditExportContractLimits.MaxEventsPerPage + 1)
            .Select(sequence => ExampleEvent(sequence))
            .ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DirectiveAuditExportPage(
            AuditExportContract.Name,
            AuditExportContract.Version,
            "acme-delivery",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            afterSequence: 0,
            nextAfterSequence: tooMany[^1].Sequence,
            isTerminal: false,
            tooMany));
    }

    [Fact]
    public void Canonical_result_is_json_hash_verified_and_size_bounded()
    {
        const string content = """
            {"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","OrganizationId":"acme-delivery","From":{"kind":"position","positionId":"bug-triage"},"To":{"kind":"position","positionId":"delivery-lead"},"Thread":"11111111-1111-1111-1111-111111111111","Priority":"normal","SchemaVersion":1,"SentAt":"2026-07-30T08:00:02+00:00","Deadline":null,"AboutDirectiveId":"22222222-2222-2222-2222-222222222222","Kind":"done","Body":"Completed."}
            """;
        var result = AuditExportResult.Create("Report", 1, content);

        Assert.Equal(416, result.ContentLengthBytes);
        Assert.Equal(
            "7d35a8caf232ba8c853ff4a4e34471d243c5caf3820c0f3e07dc77bb319223e0",
            result.Sha256);

        Assert.Throws<ArgumentException>(() => new AuditExportResult(
            "Report",
            1,
            AuditExportContract.ResultMediaType,
            result.ContentLengthBytes,
            new string('0', 64),
            content));

        var oversized = "{\"body\":\"" +
            new string('x', AuditExportContractLimits.MaxResultContentBytes) +
            "\"}";
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuditExportResult.Create("Report", 1, oversized));
    }

    [Fact]
    public void Accepted_observation_is_optional_hash_verified_and_independently_bounded()
    {
        const string content =
            "{\"dimensions\":{\"missing-information\":[\"environment\"],\"severity\":[\"medium\"]}}";
        var observation = AuditExportAcceptedObservation.Create(1, content);
        var result = AuditExportResult.Create(
            "Escalation",
            1,
            "{\"Context\":\"Authoritative fail-safe.\"}",
            observation);

        Assert.Equal(
            AuditExportContract.AcceptedObservationMediaType,
            result.AcceptedObservation!.MediaType);
        Assert.Equal(content, result.AcceptedObservation.Content);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetByteCount(content),
            result.AcceptedObservation.ContentLengthBytes);
        Assert.Throws<ArgumentException>(() => new AuditExportAcceptedObservation(
            1,
            AuditExportContract.AcceptedObservationMediaType,
            observation.ContentLengthBytes,
            new string('0', 64),
            content));

        var oversized = "{\"dimensions\":{\"x\":[\"" +
            new string('x', AuditExportContractLimits.MaxAcceptedObservationContentBytes) +
            "\"]}}";
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuditExportAcceptedObservation.Create(1, oversized));
    }

    [Fact]
    public void Contracts_project_is_dependency_free_and_surface_is_evaluation_neutral()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Hive.Contracts",
            "Hive.Contracts.csproj"));
        var dependencies = project
            .Descendants()
            .Where(element => element.Name.LocalName is
                "ProjectReference" or
                "PackageReference" or
                "FrameworkReference" or
                "Reference")
            .ToArray();
        Assert.Empty(dependencies);

        var assembly = typeof(DirectiveAuditExportPage).Assembly;
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => (reference.Name ?? string.Empty)
                .StartsWith("Hive.", StringComparison.Ordinal));

        var publicNames = assembly
            .GetExportedTypes()
            .SelectMany(type => new[] { type.Name }
                .Concat(type.GetMembers(
                        BindingFlags.Instance |
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.DeclaredOnly)
                    .Select(member => member.Name)))
            .ToArray();
        var forbiddenTerms = new[]
        {
            "Evaluation",
            "Rubric",
            "Dimension",
            "Scorer",
            "RunId",
            "CaseId",
            "Partition",
        };

        Assert.DoesNotContain(
            publicNames,
            name => forbiddenTerms.Any(term =>
                name.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static DirectiveAuditExportPage ExamplePage()
    {
        const string resultContent = """
            {"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","OrganizationId":"acme-delivery","From":{"kind":"position","positionId":"bug-triage"},"To":{"kind":"position","positionId":"delivery-lead"},"Thread":"11111111-1111-1111-1111-111111111111","Priority":"normal","SchemaVersion":1,"SentAt":"2026-07-30T08:00:02+00:00","Deadline":null,"AboutDirectiveId":"22222222-2222-2222-2222-222222222222","Kind":"done","Body":"Completed."}
            """;
        var events = new[]
        {
            new AuditExportEvent(
                1,
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 30, 8, 0, 1, TimeSpan.Zero),
                "SubmissionReceived",
                "Accepted",
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                positionId: "bug-triage",
                messageType: "Directive",
                attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["redactions"] = "directive.objective,directive.context",
                    ["source"] = "api",
                }),
            new AuditExportEvent(
                2,
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                new DateTimeOffset(2026, 7, 30, 8, 0, 2, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 30, 8, 0, 3, TimeSpan.Zero),
                "GatewayCostRecorded",
                "Completed",
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                positionId: "bug-triage",
                provider: new AuditExportProvider("openai", "gpt-example"),
                usage: new AuditExportUsage(120, 30, 150, estimated: false),
                cost: new AuditExportCost(
                    0.0012m,
                    "USD",
                    estimated: false,
                    "pricing-v1",
                    1_000_000,
                    1m,
                    2m),
                latencyMilliseconds: 950,
                attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["operation"] = "directive-inference",
                }),
        };

        return new DirectiveAuditExportPage(
            AuditExportContract.Name,
            AuditExportContract.Version,
            "acme-delivery",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            afterSequence: 0,
            nextAfterSequence: 2,
            isTerminal: true,
            events,
            AuditExportResult.Create("Report", 1, resultContent));
    }

    private static AuditExportEvent ExampleEvent(int sequence) =>
        new(
            sequence,
            Guid.Parse($"{sequence:X8}-0000-0000-0000-000000000001"),
            new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero)
                .AddSeconds(sequence),
            new DateTimeOffset(2026, 7, 30, 8, 1, 0, TimeSpan.Zero)
                .AddSeconds(sequence),
            "Stage",
            "Completed",
            Guid.Parse("44444444-4444-4444-4444-444444444444"));

    private static string FixturePath => Path.Combine(
        RepositoryRoot,
        "tests",
        "Hive.Tests",
        "Fixtures",
        "AuditExport",
        "directive-audit-export.v1.json");

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
