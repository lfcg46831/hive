using Hive.Domain.Governance;

namespace Hive.Tests;

public sealed class ActionDomainCatalogModelTests
{
    [Fact]
    public void Catalog_model_preserves_required_action_domain_blocks()
    {
        var key = AuthorityKey.From("delivery.release-prod");
        var predicate = new ActionDomainMatchPredicate(
            ActionDomainActionKind.Tool,
            new Dictionary<string, object>
            {
                ["tool"] = "http",
                ["method"] = "POST",
                ["url"] = "https://ci.acme.pt/pipelines/*/promote",
                ["amount_eur"] = 100m,
            });
        var domain = new ActionDomain(
            key,
            "Promover codigo ou configuracao para producao",
            ActionDomainGate.HumanApproval,
            [predicate]);

        var catalog = new ActionDomainCatalog(
            version: 1,
            defaults: new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
            domains: [domain]);

        Assert.Equal(1, catalog.Version);
        Assert.Equal(ActionDomainGate.Escalate, catalog.Defaults.UnmatchedAction);
        var loadedDomain = Assert.Single(catalog.Domains);
        Assert.Same(key, loadedDomain.Key);
        Assert.Equal("Promover codigo ou configuracao para producao", loadedDomain.Description);
        Assert.Equal(ActionDomainGate.HumanApproval, loadedDomain.Gate);

        var loadedPredicate = Assert.Single(loadedDomain.Match);
        Assert.Equal(ActionDomainActionKind.Tool, loadedPredicate.Action);
        Assert.Equal("http", loadedPredicate.Attributes["tool"]);
        Assert.Equal("POST", loadedPredicate.Attributes["method"]);
        Assert.Equal("https://ci.acme.pt/pipelines/*/promote", loadedPredicate.Attributes["url"]);
        Assert.Equal(100m, loadedPredicate.Attributes["amount_eur"]);
    }

    [Fact]
    public void Authority_key_requires_namespaced_non_normalized_value()
    {
        var key = AuthorityKey.From("governance.authorize.finance");

        Assert.Equal("governance.authorize.finance", key.Value);
        Assert.Equal("governance.authorize.finance", key.ToString());
        Assert.Equal(key, AuthorityKey.From("governance.authorize.finance"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("delivery")]
    [InlineData(".release-prod")]
    [InlineData("delivery.")]
    [InlineData("delivery..release-prod")]
    [InlineData(" delivery.release-prod")]
    [InlineData("delivery.release-prod ")]
    [InlineData("delivery.release prod")]
    public void Authority_key_rejects_values_that_are_not_namespaced_tokens(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => AuthorityKey.From(value!));
    }

    [Fact]
    public void Records_reject_missing_required_blocks_instead_of_applying_defaults()
    {
        var key = AuthorityKey.From("delivery.bug-triage");
        var defaults = new ActionDomainCatalogDefaults(ActionDomainGate.Escalate);
        var domain = new ActionDomain(
            key,
            "Classificar bugs",
            ActionDomainGate.Decide,
            []);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ActionDomainCatalog(version: 0, defaults, [domain]));
        Assert.Throws<ArgumentNullException>(
            () => new ActionDomainCatalog(version: 1, defaults: null!, [domain]));
        Assert.Throws<ArgumentNullException>(
            () => new ActionDomainCatalog(version: 1, defaults, domains: null!));
        Assert.Throws<ArgumentException>(
            () => new ActionDomainCatalog(version: 1, defaults, domains: [null!]));

        Assert.Throws<ArgumentNullException>(
            () => new ActionDomain(key: null!, "Classificar bugs", ActionDomainGate.Decide, []));
        Assert.ThrowsAny<ArgumentException>(
            () => new ActionDomain(key, " ", ActionDomainGate.Decide, []));
        Assert.Throws<ArgumentException>(
            () => new ActionDomain(key, "Classificar bugs", (ActionDomainGate)0, []));
        Assert.Throws<ArgumentNullException>(
            () => new ActionDomain(key, "Classificar bugs", ActionDomainGate.Decide, match: null!));
        Assert.Throws<ArgumentException>(
            () => new ActionDomain(key, "Classificar bugs", ActionDomainGate.Decide, [null!]));

        Assert.Throws<ArgumentException>(
            () => new ActionDomainCatalogDefaults((ActionDomainGate)0));
        Assert.Throws<ArgumentException>(
            () => new ActionDomainMatchPredicate((ActionDomainActionKind)0, new Dictionary<string, object>()));
        Assert.Throws<ArgumentNullException>(
            () => new ActionDomainMatchPredicate(ActionDomainActionKind.Tool, attributes: null!));
        Assert.ThrowsAny<ArgumentException>(
            () => new ActionDomainMatchPredicate(
                ActionDomainActionKind.Tool,
                new Dictionary<string, object> { [" "] = "http" }));
        Assert.ThrowsAny<ArgumentException>(
            () => new ActionDomainMatchPredicate(
                ActionDomainActionKind.Tool,
                new Dictionary<string, object> { ["tool"] = null! }));
    }

    [Fact]
    public void Collections_are_snapshotted_to_keep_records_immutable()
    {
        var key = AuthorityKey.From("comms.external-official");
        var attributes = new Dictionary<string, object>
        {
            ["tool"] = "email",
            ["recipient"] = "external",
        };
        var predicate = new ActionDomainMatchPredicate(ActionDomainActionKind.Tool, attributes);
        var match = new List<ActionDomainMatchPredicate> { predicate };
        var domain = new ActionDomain(
            key,
            "Comunicacao oficial para fora da organizacao",
            ActionDomainGate.Escalate,
            match);
        var domains = new List<ActionDomain> { domain };

        var catalog = new ActionDomainCatalog(
            version: 1,
            defaults: new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
            domains);

        attributes["tool"] = "chat";
        attributes["new"] = "value";
        match.Clear();
        domains.Clear();

        Assert.Single(catalog.Domains);
        var loadedPredicate = Assert.Single(catalog.Domains[0].Match);
        Assert.Equal("email", loadedPredicate.Attributes["tool"]);
        Assert.False(loadedPredicate.Attributes.ContainsKey("new"));
    }
}
