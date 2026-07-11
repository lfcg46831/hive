using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Governance;
using Hive.Infrastructure.Organization.Configuration;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class ExampleOrganizationActionGateIntegrationTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Can_decide_allows_an_action_declared_under_the_example_position_authority()
    {
        var fixture = LoadExample();
        var context = fixture.Context("bug-triage");
        var key = AuthorityKey.From("delivery.bug-triage");

        var result = await fixture.Gate.EvaluateAsync(
            context,
            AiAgentActionCandidate.ForTool(
                new AiToolCall("call-allowed", "jira"),
                ActingUnderDeclaration.Declared(key)));

        Assert.Equal(AiAgentActionGateOutcome.Allowed, result.Outcome);
        Assert.Equal(key, result.Resolution!.AllowedAuthorityKey);
        Assert.Null(result.Retention);
    }

    [Fact]
    public async Task Objective_catalog_predicate_retains_the_action_even_with_valid_can_decide()
    {
        var fixture = LoadExample();
        var context = fixture.Context("bug-triage");

        var result = await fixture.Gate.EvaluateAsync(
            context,
            AiAgentActionCandidate.ForTool(
                new AiToolCall("call-objective", "email.send"),
                ActingUnderDeclaration.Declared(
                    AuthorityKey.From("delivery.bug-triage"))));

        Assert.Equal(AiAgentActionGateOutcome.RetainedForEscalation, result.Outcome);
        Assert.Equal(ActionGateResolution.ObjectiveEscalationCode, result.Code);
        Assert.Equal(
            AuthorityKey.From("comms.external-official"),
            Assert.Single(result.Resolution!.Matches).Key);
        var escalation = Assert.IsType<Escalation>(
            Assert.Single(result.Retention!.GovernanceMessages));
        Assert.Equal(
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            escalation.To);
    }

    [Fact]
    public async Task Example_override_tightens_objective_gate_to_human_approval()
    {
        var fixture = LoadExample();
        var context = fixture.Context("delivery-lead");

        var result = await fixture.Gate.EvaluateAsync(
            context,
            AiAgentActionCandidate.ForTool(
                new AiToolCall("call-approval", "email.send"),
                ActingUnderDeclaration.Declared(
                    AuthorityKey.From("delivery.bug-triage"))));

        Assert.Equal(AiAgentActionGateOutcome.RetainedForHumanApproval, result.Outcome);
        Assert.Equal(ActionGateOutcome.HumanApprovalRequired, result.Resolution!.Outcome);
        var requirement = Assert.Single(result.Resolution.RequiredApprovals);
        Assert.Equal("ceo", requirement.Approver);
        Assert.Equal(
            AuthorityKey.From("comms.external-official"),
            Assert.Single(requirement.AuthorityKeys));
        var request = Assert.IsType<ApprovalRequest>(
            Assert.Single(result.Retention!.GovernanceMessages));
        Assert.Equal(new PositionEndpointRef(PositionId.From("ceo")), request.To);
    }

    [Fact]
    public async Task Missing_declaration_without_objective_match_escalates_by_catalog_default()
    {
        var fixture = LoadExample();
        var context = fixture.Context("bug-triage");

        var result = await fixture.Gate.EvaluateAsync(
            context,
            AiAgentActionCandidate.ForTool(
                new AiToolCall("call-unmatched", "jira"),
                ActingUnderDeclaration.Missing()));

        Assert.Equal(AiAgentActionGateOutcome.RetainedForEscalation, result.Outcome);
        Assert.Equal(ActionGateResolution.UnmatchedActionDefaultCode, result.Code);
        Assert.Empty(result.Resolution!.Matches);
        Assert.IsType<Escalation>(Assert.Single(result.Retention!.GovernanceMessages));
    }

    private static ExampleFixture LoadExample()
    {
        var directory = Path.Combine(
            RepositoryRoot,
            "config",
            "organizations",
            "acme-delivery");
        var organizationResult = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(directory, "organization.yaml"));
        var catalogResult = new ActionDomainCatalogParser().ParseFile(
            Path.Combine(directory, "action-domains.yaml"));

        Assert.True(
            organizationResult.IsSuccess,
            string.Join(Environment.NewLine, organizationResult.Errors));
        Assert.True(
            catalogResult.IsSuccess,
            string.Join(Environment.NewLine, catalogResult.Errors));

        var organization = organizationResult.Configuration!;
        var catalog = catalogResult.Catalog!;
        var recipientScope = ActionAttributeDefinition.Derived(
            "recipient_scope",
            ActionAttributeValueKind.String,
            [ActionAttributeValue.FromString("internal"), ActionAttributeValue.FromString("external")]);
        var binding = new ActionDomainCatalogBinding(
            authorities: organization.Positions
                .Where(position => position.Occupant.Authority is not null)
                .Select(position => new ActionDomainAuthorityBinding(
                    $"positions[{position.Id.Value}].authority",
                    position.Occupant.Authority!.CanDecide,
                    position.Occupant.Authority.Overrides
                        .Select(item => new ActionDomainAuthorityOverride(
                            item.Key,
                            item.Gate,
                            item.Approver))
                        .ToArray()))
                .ToArray(),
            declaredApprovers: organization.Positions
                .Select(position => position.Id.Value)
                .ToArray(),
            actionContracts:
            [
                ActionDomainActionContract.ForTool("jira"),
                ActionDomainActionContract.ForTool("email.send", [recipientScope]),
            ],
            actionExtractors:
            [
                ActionAttributeExtractorRegistration.ForTool(
                    "email.send",
                    ExternalRecipientScopeExtractor.Instance),
            ]);
        var validation = ActionDomainCatalogValidator.Validate(catalog, binding);

        Assert.True(
            validation.IsValid,
            string.Join(Environment.NewLine, validation.Errors.Select(error =>
                $"{error.Path}: {error.Code}: {error.Message}")));

        var gate = new AiAgentActionGate(
            catalog,
            binding,
            ExampleApprovalResolver.Instance,
            NoopJourneyAuditLog.Instance,
            () => At,
            () => At);

        return new ExampleFixture(organization, gate);
    }

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

    private sealed record ExampleFixture(
        OrganizationConfiguration Organization,
        AiAgentActionGate Gate)
    {
        public AiDirectiveExecutionContext Context(string positionId)
        {
            var position = Organization.Positions.Single(item => item.Id.Value == positionId);
            var entity = PositionEntityId.From(Organization.Organization.Id, position.Id);
            EndpointRef source = position.ReportsTo is { } superior
                ? new PositionEndpointRef(superior)
                : new OrganizationOwnerEndpointRef();
            var directive = new OrgDirective(
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001112")),
                Organization.Organization.Id,
                source,
                new PositionEndpointRef(position.Id),
                ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001112")),
                Priority.High,
                schemaVersion: 1,
                sentAt: At,
                deadline: At.AddHours(2),
                DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001112")),
                parentDirectiveId: null,
                objective: "Exercise the example authority gate.",
                context: "Integration fixture.");
            var authority = position.Occupant.Authority!;
            var runtime = new PositionRuntimeConfiguration(
                new PositionConfigurationStamp(1, "sha256:example-action-gate"),
                Organization.Organization.Id,
                position.Id,
                new PositionRuntimeDescriptor(
                    position.Unit,
                    position.ReportsTo,
                    position.Name,
                    position.Timezone),
                new OccupantRuntimeConfiguration(
                    OccupantType.AiAgent,
                    identityPromptRef: position.Occupant.IdentityPromptRef,
                    aiGateway: new AiPositionRuntimeConfiguration(
                        new AiProviderMetadata("stub", "deterministic"),
                        new AiModelParameters(maxOutputTokens: 256),
                        TimeSpan.FromSeconds(15),
                        maxIterations: 2),
                    identityPrompt: new IdentityPromptRuntimeConfiguration(
                        position.Occupant.IdentityPromptRef!,
                        $"prompts/{position.Occupant.IdentityPromptRef}.md",
                        "Example integration identity prompt.")),
                new PositionAuthorityRuntimeConfiguration(
                    authority.CanDecide.Select(key => key.Value),
                    authority.Overrides.Select(item =>
                        new PositionAuthorityOverrideRuntimeConfiguration(
                            item.Key.Value,
                            item.Gate,
                            item.Approver))));
            var request = AiDirectiveProcessingRequest.Create(
                entity,
                runtime,
                PositionState.Restore(new PositionSnapshot(At)),
                OccupantId.From($"{positionId}-agent"),
                directive);

            return AiDirectiveExecutionContext.From(request);
        }
    }

    private sealed class ExternalRecipientScopeExtractor : IActionAttributeExtractor
    {
        public static ExternalRecipientScopeExtractor Instance { get; } = new();

        public ActionAttributeExtractorOutput Extract(ActionAttributeExtractionRequest request) =>
            ActionAttributeExtractorOutput.Success(
                new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
                {
                    ["recipient_scope"] = ActionAttributeValue.FromString("external"),
                });
    }

    private sealed class ExampleApprovalResolver : IAiActionApprovalResolver
    {
        public static ExampleApprovalResolver Instance { get; } = new();

        public ValueTask<AiActionApprovalResolution> ResolveAsync(
            AiActionApprovalResolutionQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                AiActionApprovalResolution.Resolved(
                    new PositionEndpointRef(PositionId.From(query.RequiredApprover!)),
                    ApprovalPolicyRef.From("action-domain-" + query.RequiredApprover)));
        }
    }
}
