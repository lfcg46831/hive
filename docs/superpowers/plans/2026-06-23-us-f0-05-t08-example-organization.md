# US-F0-05-T08 Example Organization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the minimal tracked Engineering/Delivery organization required by US-F0-05-T08 and prove that its YAML and prompt references are valid.

**Architecture:** Treat `config/organizations/acme-delivery/` as the GitOps source artifact defined by bible §4.8 and `docs/configuration.md`. A repository-level test loads the real `organization.yaml` through `OrganizationConfigurationParser`, runs all three semantic validators, checks the minimal vertical-slice content, and verifies that each prompt catalog path resolves to a non-empty file inside the organization directory.

**Tech Stack:** .NET 8, xUnit, YamlDotNet through `OrganizationConfigurationParser`, repository-tracked YAML and Markdown.

---

### Task 1: Specify the example organization contract with a failing repository test

**Files:**
- Create: `tests/Hive.Tests/ExampleOrganizationConfigurationTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Hive.Domain.Organization.Configuration.Validation;
using Hive.Infrastructure.Organization.Configuration;

namespace Hive.Tests;

public sealed class ExampleOrganizationConfigurationTests
{
    private const string OrganizationId = "acme-delivery";

    [Fact]
    public void Example_organization_parses_and_satisfies_the_minimal_F0_contract()
    {
        var result = new OrganizationConfigurationParser().ParseFile(OrganizationFile);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        var configuration = result.Configuration!;

        Assert.Equal(OrganizationId, configuration.Organization.Id.Value);
        Assert.Equal("raiz", configuration.Organization.RootUnit.Value);
        Assert.Equal(2, configuration.Units.Count);
        Assert.Equal(2, configuration.Positions.Count);
        Assert.Equal(2, configuration.Prompts.Count);

        Assert.True(OrganizationConfigurationUniquenessValidator.Validate(configuration).IsValid);
        Assert.True(OrganizationConfigurationCrossReferenceValidator.Validate(configuration).IsValid);
        Assert.True(OrganizationConfigurationStructuralValidator.Validate(configuration).IsValid);

        var deliveryLead = configuration.Positions.Single(position => position.Id.Value == "delivery-lead");
        Assert.Equal("ceo", deliveryLead.ReportsTo!.Value);
        Assert.NotNull(deliveryLead.Occupant.Ai);
        Assert.NotNull(deliveryLead.Occupant.Authority);
        Assert.Single(deliveryLead.Occupant.Schedule);
    }

    [Fact]
    public void Example_prompt_catalog_resolves_to_non_empty_files_inside_its_directory()
    {
        var result = new OrganizationConfigurationParser().ParseFile(OrganizationFile);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        foreach (var prompt in result.Configuration!.Prompts)
        {
            var path = Path.GetFullPath(Path.Combine(OrganizationDirectory, prompt.Path));
            Assert.StartsWith(OrganizationDirectory + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
            Assert.True(File.Exists(path), $"Prompt '{prompt.Id}' was not found at '{path}'.");
            Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(path)));
        }
    }

    private static string OrganizationFile => Path.Combine(OrganizationDirectory, "organization.yaml");

    private static string OrganizationDirectory =>
        Path.Combine(RepositoryRoot, "config", "organizations", OrganizationId);

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
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~ExampleOrganizationConfigurationTests -v minimal
```

Expected: FAIL because `config/organizations/acme-delivery/organization.yaml` does not exist.

### Task 2: Add the minimal Engineering/Delivery GitOps artifact

**Files:**
- Create: `config/organizations/acme-delivery/organization.yaml`
- Create: `config/organizations/acme-delivery/prompts/ceo-v1.md`
- Create: `config/organizations/acme-delivery/prompts/engineer-v1.md`
- Test: `tests/Hive.Tests/ExampleOrganizationConfigurationTests.cs`

- [ ] **Step 1: Add the canonical organization document**

```yaml
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
    timezone: Europe/Lisbon
    occupant:
      type: ai-agent
      identity_prompt_ref: ceo-v1
      ai:
        provider: stub
        model: deterministic
        processing: interactive
      authority:
        can_decide: ["prioridades-trimestrais"]
        must_escalate: ["compromissos-orcamentais"]
        requires_human_approval: ["mudancas-de-estrutura"]
  - id: delivery-lead
    name: Delivery Lead
    unit: engenharia
    reports_to: ceo
    timezone: Europe/Lisbon
    occupant:
      type: ai-agent
      identity_prompt_ref: engineer-v1
      ai:
        provider: stub
        model: deterministic
        processing: interactive
      schedule:
        - id: relatorio-diario
          cron: "0 55 17 * * MON-FRI"
          instruction: "Compilar e enviar relatorio diario ao superior"
      authority:
        can_decide: ["triagem-de-bugs"]
        must_escalate: ["risco-de-prazo-critico"]
        requires_human_approval: ["release-em-producao"]
```

- [ ] **Step 2: Add the CEO identity prompt**

```markdown
# CEO

You lead the Engineering/Delivery organization. Set priorities, delegate work to the Delivery Lead, and require concise evidence-based status reports.

Stay within the authority declared for this position. Escalate commitments outside that authority to the Organization Owner; the prompt never overrides HIVE policy enforcement.
```

- [ ] **Step 3: Add the Delivery Lead identity prompt**

```markdown
# Delivery Lead

You lead the Engineering/Delivery unit. Triage incoming bugs, turn actionable work into clear directives, track progress, and report outcomes to the CEO.

Use `Report` for progress or completion and `Escalation` for actionable blockers or decisions outside your authority. Stay within the authority declared for this position; the prompt never overrides HIVE policy enforcement.
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~ExampleOrganizationConfigurationTests -v minimal
```

Expected: PASS, 2 tests successful.

- [ ] **Step 5: Run the complete test project**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore -v minimal
```

Expected: PASS with no failed tests.

- [ ] **Step 6: Review the final diff and prepare the commit message**

Run:

```powershell
git diff --check
git diff -- tests/Hive.Tests/ExampleOrganizationConfigurationTests.cs config/organizations/acme-delivery
```

Expected: no whitespace errors; the diff contains only the test and the three example-organization files.

Commit message:

```text
feat(config): add minimal Engineering/Delivery example organization (US-F0-05-T08)
```
