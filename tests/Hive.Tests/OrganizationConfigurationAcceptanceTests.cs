using Hive.Domain.Identity;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Tests;

/// <summary>
/// End-to-end acceptance tests for US-F0-05 (US-F0-05-T12): they drive a YAML document through the
/// full load pipeline — parse (US-F0-05-T04) then import (US-F0-05-T09) into the in-memory registry —
/// and assert the §4.8 acceptance criteria over the named scenarios enumerated by the task: a valid
/// document materializes; a malformed document never parses; broken references, parent cycles,
/// missing/duplicate unit leadership, a missing root leadership and a missing <c>OrganizationOwner</c>
/// are each rejected as <see cref="OrganizationImportStatus.Invalid"/> with the structured code that
/// owns the rule and leave the registry empty; and a re-imported, unchanged document is an idempotent
/// no-op. The per-validator unit suites (US-F0-05-T05–T07) own the exhaustive rule coverage; this
/// suite proves the pipeline wires them together and refuses to materialize an invalid organization.
/// </summary>
public sealed class OrganizationConfigurationAcceptanceTests
{
    private const string FilePath = "config/organizations/acme-delivery/organization.yaml";

    private static readonly DateTimeOffset ImportAt =
        new(2026, 6, 24, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A self-contained, semantically valid Engineering/Delivery document mirroring the F0 example.
    /// The invalid scenarios are derived from this base by a single, unique textual substitution so each
    /// test isolates exactly one broken rule.
    /// </summary>
    private const string ValidYaml = """
        organization:
          id: acme-delivery
          name: ACME Engenharia/Delivery
          root_unit: raiz
          owner:
            type: human
            ref: owner@acme.pt
        prompts:
          - id: ceo-v1
            path: prompts/ceo-v1.md
          - id: engineer-v1
            path: prompts/engineer-v1.md
        units:
          - id: raiz
            name: ACME
            parent: null
            leadership: ceo
          - id: engenharia
            name: Engenharia/Delivery
            parent: raiz
            leadership: delivery-lead
        positions:
          - id: ceo
            name: CEO
            unit: raiz
            reports_to: null
            occupant:
              type: ai-agent
              identity_prompt_ref: ceo-v1
          - id: delivery-lead
            name: Delivery Lead
            unit: engenharia
            reports_to: ceo
            occupant:
              type: ai-agent
              identity_prompt_ref: engineer-v1
              schedule:
                - id: relatorio-diario
                  cron: "0 55 17 ? * MON-FRI"
                  instruction: "Compilar e enviar relatorio diario ao superior"
        """;

    [Fact]
    public async Task Valid_document_is_parsed_validated_and_materialized()
    {
        var (registry, result) = await ImportAsync(ValidYaml);

        Assert.Equal(OrganizationImportStatus.Applied, result.Status);
        Assert.Empty(result.ValidationErrors);

        var snapshot = result.Snapshot!;
        Assert.Equal(1, snapshot.Version);
        Assert.StartsWith("sha256:", snapshot.Fingerprint, StringComparison.Ordinal);
        Assert.Equal(2, snapshot.Units.Count);
        Assert.Equal(2, snapshot.Positions.Count);
        Assert.Single(snapshot.Schedules);

        // The materialized registry serves the live command relations of §4.1/§4.2.
        IOrganizationRelations relations = registry;
        var organizationId = OrganizationId.From("acme-delivery");
        Assert.Equal(
            PositionId.From("ceo"),
            await relations.GetRootUnitLeadershipAsync(organizationId));
        Assert.Equal(
            PositionId.From("ceo"),
            await relations.GetDirectSuperiorAsync(organizationId, PositionId.From("delivery-lead")));
    }

    [Fact]
    public void Invalid_yaml_never_parses_and_nothing_is_materialized()
    {
        // A malformed document (unterminated flow sequence) is a parse-level failure (US-F0-05-T04):
        // it can never reach the importer, so the registry is never touched.
        var parser = new OrganizationConfigurationParser();
        var parseResult = parser.Parse("organization: [unterminated\n  id: acme\n", FilePath);

        Assert.False(parseResult.IsSuccess);
        Assert.Null(parseResult.Configuration);
        Assert.NotEmpty(parseResult.Errors);
        Assert.All(parseResult.Errors, error => Assert.Equal(FilePath, error.FilePath));
    }

    [Fact]
    public async Task Broken_reference_is_rejected_and_not_materialized()
    {
        // A dangling positions[].reports_to is an unresolved cross-reference (US-F0-05-T06).
        var yaml = Mutate(ValidYaml, "reports_to: ceo", "reports_to: nonexistent");

        await AssertRejectedAsync(yaml, "reports-to-position-not-found");
    }

    [Fact]
    public async Task Parent_cycle_is_rejected_and_not_materialized()
    {
        // Rooting the root unit at its own descendant closes a cycle in the units[].parent tree
        // (US-F0-05-T07).
        var yaml = Mutate(ValidYaml, "parent: null", "parent: engenharia");

        await AssertRejectedAsync(yaml, "unit-parent-cycle");
    }

    [Fact]
    public async Task Missing_unit_leadership_is_rejected_and_not_materialized()
    {
        // A unit led by a position that does not exist (US-F0-05-T06).
        var yaml = Mutate(ValidYaml, "leadership: delivery-lead", "leadership: ghost-position");

        await AssertRejectedAsync(yaml, "leadership-position-not-found");
    }

    [Fact]
    public async Task Duplicate_unit_leadership_is_rejected_and_not_materialized()
    {
        // A single position leading two units violates "exactly one leadership per unit" (US-F0-05-T07).
        var yaml = Mutate(ValidYaml, "leadership: delivery-lead", "leadership: ceo");

        await AssertRejectedAsync(yaml, "position-leads-multiple-units");
    }

    [Fact]
    public async Task Missing_root_leadership_is_rejected_and_not_materialized()
    {
        // organization.root_unit pointing at no declared unit leaves the organization without a root
        // leadership (US-F0-05-T06).
        var yaml = Mutate(ValidYaml, "root_unit: raiz", "root_unit: inexistente");

        await AssertRejectedAsync(yaml, "root-unit-not-found");
    }

    [Fact]
    public async Task Missing_organization_owner_is_rejected_and_not_materialized()
    {
        // The OrganizationOwner is required (§4.2). The parser requires the owner.ref key, so a blank
        // owner is exercised over the typed model: the importer must reject it before materializing.
        var configuration = Parse(ValidYaml);
        var blankOwner = new OrganizationConfiguration(
            new OrganizationHeader(
                configuration.Organization.Id,
                configuration.Organization.RootUnit,
                new OwnerConfiguration(configuration.Organization.Owner.Type, "   "),
                configuration.Organization.Name),
            configuration.Units,
            configuration.Positions,
            configuration.Prompts);

        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(registry, new FixedClock(ImportAt));

        var result = await importer.ImportAsync(blankOwner);

        Assert.Equal(OrganizationImportStatus.Invalid, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Contains(result.ValidationErrors, error => error.Code == "organization-owner-required");
        Assert.False(registry.TryGetSnapshot(configuration.Organization.Id, out _));
    }

    [Fact]
    public async Task Reimporting_the_unchanged_document_is_an_idempotent_no_op()
    {
        var registry = new InMemoryOrganizationRegistry();
        var clock = new FixedClock(ImportAt);
        var importer = new OrganizationConfigurationImporter(registry, clock);

        var first = await importer.ImportAsync(Parse(ValidYaml));
        Assert.Equal(OrganizationImportStatus.Applied, first.Status);

        // A second import an hour later of the identical document must not create a new version, change
        // the fingerprint, or move functional timestamps (US-F0-05-T09c).
        clock.UtcNow = ImportAt.AddHours(1);
        var second = await importer.ImportAsync(Parse(ValidYaml));

        Assert.Equal(OrganizationImportStatus.NoChanges, second.Status);
        Assert.Same(first.Snapshot, second.Snapshot);
        Assert.Equal(first.Snapshot!.Fingerprint, second.Snapshot!.Fingerprint);
        Assert.Equal(1, second.Snapshot.Version);
        Assert.Equal(ImportAt, second.Snapshot.ImportedAt);
        Assert.Empty(second.Plan!.Changes);
    }

    /// <summary>
    /// Asserts that <paramref name="yaml"/> parses but is rejected by the importer with
    /// <paramref name="expectedCode"/>, producing neither a plan nor a published snapshot.
    /// </summary>
    private static async Task AssertRejectedAsync(string yaml, string expectedCode)
    {
        var (registry, result) = await ImportAsync(yaml);

        Assert.Equal(OrganizationImportStatus.Invalid, result.Status);
        Assert.Null(result.Plan);
        Assert.Null(result.Snapshot);
        Assert.Contains(result.ValidationErrors, error => error.Code == expectedCode);
        Assert.False(registry.TryGetSnapshot(OrganizationId.From("acme-delivery"), out _));
    }

    private static async Task<(InMemoryOrganizationRegistry Registry, OrganizationImportResult Result)>
        ImportAsync(string yaml)
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(registry, new FixedClock(ImportAt));
        var result = await importer.ImportAsync(Parse(yaml));
        return (registry, result);
    }

    private static OrganizationConfiguration Parse(string yaml)
    {
        var parseResult = new OrganizationConfigurationParser().Parse(yaml, FilePath);
        Assert.True(
            parseResult.IsSuccess,
            string.Join(Environment.NewLine, parseResult.Errors.Select(error => error.ToString())));
        return parseResult.Configuration!;
    }

    /// <summary>
    /// Applies a single, unique textual substitution to the valid base document, asserting the token is
    /// present exactly once so the derived scenario isolates one broken rule.
    /// </summary>
    private static string Mutate(string yaml, string token, string replacement)
    {
        var first = yaml.IndexOf(token, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Token '{token}' was not found in the base document.");
        Assert.Equal(
            -1,
            yaml.IndexOf(token, first + token.Length, StringComparison.Ordinal));
        return yaml.Remove(first, token.Length).Insert(first, replacement);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
