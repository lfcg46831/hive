using Hive.Domain.Governance;

namespace Hive.Tests;

public sealed class ActionDomainCatalogValidatorTests
{
    [Fact]
    public void Valid_catalog_and_static_bindings_pass()
    {
        var catalog = Catalog(
            Domain("delivery.bug-triage", ActionDomainGate.Decide),
            Domain(
                "comms.external-official",
                ActionDomainGate.Escalate,
                Predicate(
                    ActionDomainActionKind.Tool,
                    ("tool", "email"),
                    ("recipient", "external"))),
            Domain(
                "delivery.release-prod",
                ActionDomainGate.HumanApproval,
                Predicate(
                    ActionDomainActionKind.Tool,
                    ("tool", "http"),
                    ("method", "POST"),
                    ("url", "https://ci.acme.pt/pipelines/*/promote"))));
        var binding = new ActionDomainCatalogBinding(
            authorities:
            [
                new ActionDomainAuthorityBinding(
                    "positions[0].occupant.authority",
                    canDecide: [Key("delivery.bug-triage")],
                    overrides:
                    [
                        new ActionDomainAuthorityOverride(
                            Key("comms.external-official"),
                            ActionDomainGate.HumanApproval,
                            approver: "ceo"),
                    ]),
            ],
            declaredApprovers: ["ceo", "delivery-lead"],
            actionContracts:
            [
                ActionDomainActionContract.ForTool("email", ["tool", "recipient"]),
                ActionDomainActionContract.ForTool("http", ["tool", "method", "url"]),
            ]);

        var result = ActionDomainCatalogValidator.Validate(catalog, binding);

        Assert.True(result.IsValid, string.Join("\n", result.Errors.Select(error => error.Message)));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Catalog_keys_are_unique_and_trust_keys_must_use_decide_gate()
    {
        var catalog = Catalog(
            Domain("delivery.bug-triage", ActionDomainGate.Escalate),
            Domain("delivery.bug-triage", ActionDomainGate.Decide));

        var result = ActionDomainCatalogValidator.Validate(catalog, EmptyBinding);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error is { Code: "trust-key-gate-not-decide", Path: "domains[0].gate" });
        Assert.Contains(
            result.Errors,
            error => error is { Code: "duplicate-action-domain-key", Path: "domains[1].key" }
                && error.Message.Contains("domains[0]"));
    }

    [Fact]
    public void Unmatched_action_default_must_remain_fail_closed_escalate()
    {
        var catalog = new ActionDomainCatalog(
            version: 1,
            defaults: new ActionDomainCatalogDefaults(ActionDomainGate.Decide),
            domains: [Domain("delivery.bug-triage", ActionDomainGate.Decide)]);

        var result = ActionDomainCatalogValidator.Validate(catalog, EmptyBinding);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error is
            {
                Code: "unmatched-action-default-not-escalate",
                Path: "defaults.unmatched_action",
            });
    }

    [Fact]
    public void Authority_references_must_exist_and_overrides_can_only_tighten_the_catalog_gate()
    {
        var catalog = Catalog(
            Domain("delivery.bug-triage", ActionDomainGate.Decide),
            Domain(
                "comms.external-official",
                ActionDomainGate.Escalate,
                Predicate(
                    ActionDomainActionKind.Tool,
                    ("tool", "email"),
                    ("recipient", "external"))),
            Domain(
                "delivery.release-prod",
                ActionDomainGate.HumanApproval,
                Predicate(
                    ActionDomainActionKind.Tool,
                    ("tool", "http"),
                    ("method", "POST"))));
        var binding = new ActionDomainCatalogBinding(
            authorities:
            [
                new ActionDomainAuthorityBinding(
                    "positions[2].occupant.authority",
                    canDecide:
                    [
                        Key("delivery.bug-triage"),
                        Key("delivery.release-prod"),
                        Key("delivery.unknown"),
                    ],
                    overrides:
                    [
                        new ActionDomainAuthorityOverride(
                            Key("comms.external-official"),
                            ActionDomainGate.Decide,
                            approver: "ceo"),
                        new ActionDomainAuthorityOverride(
                            Key("delivery.release-prod"),
                            ActionDomainGate.Escalate,
                            approver: "ghost"),
                        new ActionDomainAuthorityOverride(
                            Key("delivery.missing"),
                            ActionDomainGate.HumanApproval),
                    ]),
            ],
            declaredApprovers: ["ceo"],
            actionContracts:
            [
                ActionDomainActionContract.ForTool("email", ["tool", "recipient"]),
                ActionDomainActionContract.ForTool("http", ["tool", "method"]),
            ]);

        var result = ActionDomainCatalogValidator.Validate(catalog, binding);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error is
            {
                Code: "can-decide-key-has-match",
                Path: "positions[2].occupant.authority.can_decide[1]",
            });
        Assert.Contains(
            result.Errors,
            error => error is
            {
                Code: "authority-key-not-found",
                Path: "positions[2].occupant.authority.can_decide[2]",
            });
        Assert.Contains(
            result.Errors,
            error => error is
            {
                Code: "override-gate-relaxes-minimum",
                Path: "positions[2].occupant.authority.overrides[0].gate",
            });
        Assert.Contains(
            result.Errors,
            error => error is
            {
                Code: "override-gate-relaxes-minimum",
                Path: "positions[2].occupant.authority.overrides[1].gate",
            });
        Assert.Contains(
            result.Errors,
            error => error is
            {
                Code: "override-approver-not-found",
                Path: "positions[2].occupant.authority.overrides[1].approver",
            });
        Assert.Contains(
            result.Errors,
            error => error is
            {
                Code: "authority-key-not-found",
                Path: "positions[2].occupant.authority.overrides[2].key",
            });
    }

    [Fact]
    public void Overrides_must_target_objective_keys_with_match_predicates()
    {
        var catalog = Catalog(
            Domain("delivery.bug-triage", ActionDomainGate.Decide),
            Domain(
                "comms.external-official",
                ActionDomainGate.Escalate,
                Predicate(
                    ActionDomainActionKind.Tool,
                    ("tool", "email"),
                    ("recipient", "external"))));
        var binding = new ActionDomainCatalogBinding(
            authorities:
            [
                new ActionDomainAuthorityBinding(
                    "positions[1].occupant.authority",
                    overrides:
                    [
                        new ActionDomainAuthorityOverride(
                            Key("delivery.bug-triage"),
                            ActionDomainGate.HumanApproval,
                            approver: "ceo"),
                    ]),
            ],
            declaredApprovers: ["ceo"],
            actionContracts:
            [
                ActionDomainActionContract.ForTool("email", ["tool", "recipient"]),
            ]);

        var result = ActionDomainCatalogValidator.Validate(catalog, binding);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error is
            {
                Code: "override-key-has-no-match",
                Path: "positions[1].occupant.authority.overrides[0].key",
            });
    }

    [Fact]
    public void Match_predicates_can_only_reference_attributes_declared_by_the_action_contract()
    {
        var catalog = Catalog(
            Domain(
                "delivery.release-prod",
                ActionDomainGate.HumanApproval,
                Predicate(
                    ActionDomainActionKind.Tool,
                    ("tool", "http"),
                    ("method", "POST"),
                    ("amount_eur", 100))),
            Domain(
                "comms.slack-external",
                ActionDomainGate.Escalate,
                Predicate(
                    ActionDomainActionKind.Tool,
                    ("tool", "slack"),
                    ("recipient", "external"))),
            Domain(
                "comms.report-external",
                ActionDomainGate.Escalate,
                Predicate(
                    ActionDomainActionKind.OrganizationalMessage,
                    ("message_type", "report"),
                    ("recipient", "external"))));
        var binding = new ActionDomainCatalogBinding(
            actionContracts:
            [
                ActionDomainActionContract.ForTool("http", ["tool", "method"]),
                ActionDomainActionContract.ForOrganizationalMessage(
                    "report",
                    ["message_type", "recipient"]),
            ]);

        var result = ActionDomainCatalogValidator.Validate(catalog, binding);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error is
            {
                Code: "predicate-attribute-not-declared",
                Path: "domains[0].match[0].amount_eur",
            });
        Assert.Contains(
            result.Errors,
            error => error is
            {
                Code: "action-contract-not-found",
                Path: "domains[1].match[0].tool",
            });
        Assert.DoesNotContain(
            result.Errors,
            error => error.Path.StartsWith("domains[2]", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_null_inputs()
    {
        Assert.Throws<ArgumentNullException>(
            () => ActionDomainCatalogValidator.Validate(null!, EmptyBinding));
        Assert.Throws<ArgumentNullException>(
            () => ActionDomainCatalogValidator.Validate(Catalog(), null!));
    }

    private static ActionDomainCatalogBinding EmptyBinding { get; } = new();

    private static ActionDomainCatalog Catalog(params ActionDomain[] domains) =>
        new(
            version: 1,
            defaults: new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
            domains);

    private static ActionDomain Domain(
        string key,
        ActionDomainGate gate,
        params ActionDomainMatchPredicate[] match) =>
        new(
            Key(key),
            $"Description for {key}",
            gate,
            match);

    private static ActionDomainMatchPredicate Predicate(
        ActionDomainActionKind action,
        params (string Key, object Value)[] attributes) =>
        new(
            action,
            attributes.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal));

    private static AuthorityKey Key(string value) => AuthorityKey.From(value);
}
