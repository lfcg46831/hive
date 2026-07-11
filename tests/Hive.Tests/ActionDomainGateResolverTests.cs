using Hive.Domain.Governance;

namespace Hive.Tests;

public sealed class ActionDomainGateResolverTests
{
    [Fact]
    public void Matching_uses_and_within_a_predicate_or_across_predicates_and_counts_a_domain_once()
    {
        var facts = ToolFacts(
            "email.send",
            ("recipient_scope", ActionAttributeValue.FromString("external")),
            ("urgent", ActionAttributeValue.FromBoolean(true)));
        var matchingKey = Key("comms.external-official");
        var catalog = Catalog(
            Objective(
                matchingKey,
                ActionDomainGate.HumanApproval,
                ToolPredicate(
                    "email.send",
                    ("recipient_scope", "internal"),
                    ("urgent", true)),
                ToolPredicate(
                    "email.send",
                    ("recipient_scope", "external"),
                    ("urgent", true)),
                ToolPredicate("email.send", ("recipient_scope", "external"))),
            Objective(
                Key("comms.internal-urgent"),
                ActionDomainGate.Escalate,
                ToolPredicate(
                    "email.send",
                    ("recipient_scope", "internal"),
                    ("urgent", true))),
            Objective(
                Key("messages.external-official"),
                ActionDomainGate.HumanApproval,
                MessagePredicate("Report", ("recipient_scope", "external"))));

        var resolution = Resolve(catalog, facts);

        Assert.Equal(ActionGateOutcome.HumanApprovalRequired, resolution.Outcome);
        Assert.Equal(ActionGateResolutionReason.ObjectiveHumanApproval, resolution.Reason);
        Assert.Equal(ActionGateResolution.ObjectiveHumanApprovalCode, resolution.Code);
        var match = Assert.Single(resolution.Matches);
        Assert.Equal(matchingKey, match.Key);
        var requirement = Assert.Single(resolution.RequiredApprovals);
        Assert.Null(requirement.Approver);
        Assert.Equal([matchingKey], requirement.AuthorityKeys);
        Assert.Null(resolution.AllowedAuthorityKey);
    }

    [Fact]
    public void Matching_uses_typed_canonical_values_and_literal_ordinal_text()
    {
        var facts = ToolFacts(
            "http",
            ("method", ActionAttributeValue.FromString("POST")),
            ("url", ActionAttributeValue.FromString(
                "https://ci.acme.test/pipelines/acme/promote")),
            ("confirmed", ActionAttributeValue.FromBoolean(true)),
            ("attempt", ActionAttributeValue.FromInteger(3)),
            ("amount", ActionAttributeValue.FromDecimal(3m)));
        var exactKey = Key("delivery.release-exact");
        var catalog = Catalog(
            Objective(
                exactKey,
                ActionDomainGate.Escalate,
                ToolPredicate(
                    "http",
                    ("method", "POST"),
                    ("url", "https://ci.acme.test/pipelines/acme/promote"),
                    ("confirmed", true),
                    ("attempt", 3),
                    ("amount", 3m))),
            Objective(
                Key("delivery.release-pattern"),
                ActionDomainGate.HumanApproval,
                ToolPredicate(
                    "http",
                    ("url", "https://ci.acme.test/pipelines/*/promote"))),
            Objective(
                Key("delivery.release-case-mismatch"),
                ActionDomainGate.HumanApproval,
                ToolPredicate("http", ("method", "post"))),
            Objective(
                Key("delivery.release-type-mismatch"),
                ActionDomainGate.HumanApproval,
                ToolPredicate("http", ("attempt", 3m))),
            Objective(
                Key("delivery.release-selector-mismatch"),
                ActionDomainGate.HumanApproval,
                ToolPredicate("http.other", ("method", "POST"))));

        var resolution = Resolve(catalog, facts);

        Assert.Equal(ActionGateOutcome.EscalationRequired, resolution.Outcome);
        Assert.Equal(ActionGateResolutionReason.ObjectiveEscalation, resolution.Reason);
        Assert.Equal(exactKey, Assert.Single(resolution.Matches).Key);
    }

    [Fact]
    public void Organizational_message_facts_can_match_an_objective_domain()
    {
        var key = Key("messages.completed-report");
        var facts = MessageFacts(
            "Report",
            ("report_kind", ActionAttributeValue.FromString("Done")));
        var catalog = Catalog(
            Objective(
                key,
                ActionDomainGate.Escalate,
                MessagePredicate("Report", ("report_kind", "Done"))));

        var resolution = Resolve(catalog, facts);

        Assert.Equal(ActionGateOutcome.EscalationRequired, resolution.Outcome);
        Assert.Equal(key, Assert.Single(resolution.Matches).Key);
    }

    [Fact]
    public void Human_approval_matches_win_over_escalation_and_declared_authority()
    {
        var trustKey = Key("delivery.bug-triage");
        var escalationKey = Key("delivery.release-review");
        var approvalKey = Key("delivery.release-production");
        var facts = ToolFacts(
            "deployment.promote",
            ("environment", ActionAttributeValue.FromString("production")));
        var catalog = Catalog(
            Trust(trustKey),
            Objective(
                escalationKey,
                ActionDomainGate.Escalate,
                ToolPredicate("deployment.promote", ("environment", "production"))),
            Objective(
                approvalKey,
                ActionDomainGate.HumanApproval,
                ToolPredicate("deployment.promote", ("environment", "production"))));
        var authority = Authority(canDecide: [trustKey]);

        var resolution = ActionGateResolver.Resolve(
            catalog,
            authority,
            facts,
            ActingUnderDeclaration.Declared(trustKey));

        Assert.Equal(ActionGateOutcome.HumanApprovalRequired, resolution.Outcome);
        Assert.Equal(
            ["delivery.release-production", "delivery.release-review"],
            resolution.Matches.Select(match => match.Key.Value));
        Assert.Equal(approvalKey, Assert.Single(resolution.RequiredApprovals).AuthorityKeys[0]);
        Assert.Null(resolution.AllowedAuthorityKey);
    }

    [Fact]
    public void Escalation_matches_win_over_declared_authority()
    {
        var trustKey = Key("delivery.bug-triage");
        var facts = ToolFacts(
            "deployment.promote",
            ("environment", ActionAttributeValue.FromString("staging")));
        var catalog = Catalog(
            Trust(trustKey),
            Objective(
                Key("delivery.release-review"),
                ActionDomainGate.Escalate,
                ToolPredicate("deployment.promote", ("environment", "staging"))));

        var resolution = ActionGateResolver.Resolve(
            catalog,
            Authority(canDecide: [trustKey]),
            facts,
            ActingUnderDeclaration.Declared(trustKey));

        Assert.Equal(ActionGateOutcome.EscalationRequired, resolution.Outcome);
        Assert.Equal(ActionGateResolutionReason.ObjectiveEscalation, resolution.Reason);
        Assert.Equal(ActionGateResolution.ObjectiveEscalationCode, resolution.Code);
        Assert.Null(resolution.AllowedAuthorityKey);
        Assert.Empty(resolution.RequiredApprovals);
    }

    [Fact]
    public void Human_approval_unions_equal_explicit_approvers_and_preserves_distinct_and_unresolved_requirements()
    {
        var alpha = Key("deployment.alpha");
        var beta = Key("deployment.beta");
        var delta = Key("deployment.delta");
        var gamma = Key("deployment.gamma");
        var facts = ToolFacts(
            "deployment.promote",
            ("environment", ActionAttributeValue.FromString("production")));
        var predicate = ToolPredicate(
            "deployment.promote",
            ("environment", "production"));
        var catalog = Catalog(
            Objective(gamma, ActionDomainGate.HumanApproval, predicate),
            Objective(alpha, ActionDomainGate.Escalate, predicate),
            Objective(delta, ActionDomainGate.HumanApproval, predicate),
            Objective(beta, ActionDomainGate.HumanApproval, predicate));
        var authority = Authority(
            overrides:
            [
                Override(gamma, ActionDomainGate.HumanApproval, "security-lead"),
                Override(beta, ActionDomainGate.HumanApproval, "ceo"),
                Override(alpha, ActionDomainGate.HumanApproval, "ceo"),
            ]);

        var resolution = ActionGateResolver.Resolve(
            catalog,
            authority,
            facts,
            ActingUnderDeclaration.Missing());

        Assert.Equal(ActionGateOutcome.HumanApprovalRequired, resolution.Outcome);
        Assert.Equal(
            ["deployment.alpha", "deployment.beta", "deployment.delta", "deployment.gamma"],
            resolution.Matches.Select(match => match.Key.Value));
        Assert.All(
            resolution.Matches,
            match => Assert.Equal(ActionDomainGate.HumanApproval, match.EffectiveGate));

        Assert.Collection(
            resolution.RequiredApprovals,
            unresolved =>
            {
                Assert.Null(unresolved.Approver);
                Assert.Equal([delta], unresolved.AuthorityKeys);
            },
            ceo =>
            {
                Assert.Equal("ceo", ceo.Approver);
                Assert.Equal([alpha, beta], ceo.AuthorityKeys);
            },
            security =>
            {
                Assert.Equal("security-lead", security.Approver);
                Assert.Equal([gamma], security.AuthorityKeys);
            });
    }

    [Fact]
    public void Overrides_only_tighten_domains_that_objectively_match()
    {
        var matched = Key("comms.external-send");
        var unmatched = Key("delivery.production-deploy");
        var facts = ToolFacts(
            "email.send",
            ("recipient_scope", ActionAttributeValue.FromString("external")));
        var catalog = Catalog(
            Objective(
                matched,
                ActionDomainGate.Decide,
                ToolPredicate("email.send", ("recipient_scope", "external"))),
            Objective(
                unmatched,
                ActionDomainGate.Escalate,
                ToolPredicate("deployment.promote", ("environment", "production"))));
        var authority = Authority(
            overrides:
            [
                Override(unmatched, ActionDomainGate.HumanApproval, "ceo"),
                Override(matched, ActionDomainGate.Escalate),
            ]);

        var resolution = ActionGateResolver.Resolve(
            catalog,
            authority,
            facts,
            ActingUnderDeclaration.Missing());

        Assert.Equal(ActionGateOutcome.EscalationRequired, resolution.Outcome);
        var match = Assert.Single(resolution.Matches);
        Assert.Equal(matched, match.Key);
        Assert.Equal(ActionDomainGate.Decide, match.MinimumGate);
        Assert.Equal(ActionDomainGate.Escalate, match.EffectiveGate);
        Assert.Empty(resolution.RequiredApprovals);
    }

    [Fact]
    public void Objective_decide_matches_do_not_authorize_but_a_valid_trust_declaration_does()
    {
        var objectiveKey = Key("delivery.internal-classification");
        var trustKey = Key("delivery.bug-triage");
        var facts = ToolFacts(
            "work.classify",
            ("scope", ActionAttributeValue.FromString("internal")));
        var catalog = Catalog(
            Objective(
                objectiveKey,
                ActionDomainGate.Decide,
                ToolPredicate("work.classify", ("scope", "internal"))),
            Trust(trustKey));
        var authority = Authority(canDecide: [trustKey]);

        var allowed = ActionGateResolver.Resolve(
            catalog,
            authority,
            facts,
            ActingUnderDeclaration.Declared(trustKey));
        var defaulted = ActionGateResolver.Resolve(
            catalog,
            authority,
            facts,
            ActingUnderDeclaration.Missing());

        Assert.Equal(ActionGateOutcome.Allowed, allowed.Outcome);
        Assert.Equal(ActionGateResolutionReason.DeclaredAuthority, allowed.Reason);
        Assert.Equal(ActionGateResolution.DeclaredAuthorityCode, allowed.Code);
        Assert.Equal(trustKey, allowed.AllowedAuthorityKey);
        Assert.Equal(objectiveKey, Assert.Single(allowed.Matches).Key);
        Assert.Empty(allowed.RequiredApprovals);

        Assert.Equal(ActionGateOutcome.EscalationRequired, defaulted.Outcome);
        Assert.Equal(ActionGateResolutionReason.UnmatchedActionDefault, defaulted.Reason);
        Assert.Equal(ActionGateResolution.UnmatchedActionDefaultCode, defaulted.Code);
        Assert.Null(defaulted.AllowedAuthorityKey);
    }

    [Fact]
    public void Trust_declaration_requires_position_membership_and_does_not_require_an_objective_match()
    {
        var allowedKey = Key("delivery.bug-triage");
        var otherCatalogTrustKey = Key("delivery.incident-triage");
        var catalog = Catalog(Trust(otherCatalogTrustKey), Trust(allowedKey));
        var facts = ToolFacts("work.classify");
        var firstAuthority = Authority(canDecide: [otherCatalogTrustKey, allowedKey]);
        var secondAuthority = Authority(canDecide: [allowedKey, otherCatalogTrustKey]);

        var firstAllowed = ActionGateResolver.Resolve(
            catalog,
            firstAuthority,
            facts,
            ActingUnderDeclaration.Declared(allowedKey));
        var secondAllowed = ActionGateResolver.Resolve(
            catalog,
            secondAuthority,
            facts,
            ActingUnderDeclaration.Declared(allowedKey));
        var outsidePositionAuthority = ActionGateResolver.Resolve(
            catalog,
            Authority(canDecide: [allowedKey]),
            facts,
            ActingUnderDeclaration.Declared(otherCatalogTrustKey));

        Assert.Equal(firstAllowed, secondAllowed);
        Assert.Equal(ActionGateOutcome.Allowed, firstAllowed.Outcome);
        Assert.Equal(allowedKey, firstAllowed.AllowedAuthorityKey);
        Assert.Empty(firstAllowed.Matches);
        Assert.Empty(firstAllowed.RequiredApprovals);

        Assert.Equal(ActionGateOutcome.EscalationRequired, outsidePositionAuthority.Outcome);
        Assert.Equal(
            ActionGateResolutionReason.UnmatchedActionDefault,
            outsidePositionAuthority.Reason);
        Assert.Null(outsidePositionAuthority.AllowedAuthorityKey);
    }

    [Fact]
    public void Missing_invalid_and_foreign_declarations_all_use_the_fail_closed_default()
    {
        var trustKey = Key("delivery.bug-triage");
        var catalog = Catalog(Trust(trustKey));
        var facts = ToolFacts("work.classify");
        var authority = Authority(canDecide: [trustKey]);
        var declarations = new[]
        {
            ActingUnderDeclaration.Missing(),
            ActingUnderDeclaration.Invalid(),
            ActingUnderDeclaration.Declared(Key("finance.commitments")),
        };

        foreach (var declaration in declarations)
        {
            var resolution = ActionGateResolver.Resolve(
                catalog,
                authority,
                facts,
                declaration);

            Assert.Equal(ActionGateOutcome.EscalationRequired, resolution.Outcome);
            Assert.Equal(ActionGateResolutionReason.UnmatchedActionDefault, resolution.Reason);
            Assert.Empty(resolution.Matches);
            Assert.Empty(resolution.RequiredApprovals);
            Assert.Null(resolution.AllowedAuthorityKey);
        }
    }

    [Fact]
    public void Resolution_is_structurally_equal_and_canonical_across_input_orderings()
    {
        var alpha = Key("deployment.alpha");
        var beta = Key("deployment.beta");
        var gamma = Key("deployment.gamma");
        var firstFacts = ToolFacts(
            "deployment.promote",
            ("environment", ActionAttributeValue.FromString("production")),
            ("confirmed", ActionAttributeValue.FromBoolean(true)));
        var secondFacts = ToolFacts(
            "deployment.promote",
            ("confirmed", ActionAttributeValue.FromBoolean(true)),
            ("environment", ActionAttributeValue.FromString("production")));
        var firstCatalog = Catalog(
            Objective(
                gamma,
                ActionDomainGate.HumanApproval,
                ToolPredicate(
                    "deployment.promote",
                    ("environment", "production"),
                    ("confirmed", true))),
            Objective(
                alpha,
                ActionDomainGate.HumanApproval,
                ToolPredicate(
                    "deployment.promote",
                    ("confirmed", true),
                    ("environment", "production"))),
            Objective(
                beta,
                ActionDomainGate.HumanApproval,
                ToolPredicate(
                    "deployment.promote",
                    ("environment", "production"),
                    ("confirmed", true))));
        var secondCatalog = Catalog(
            Objective(
                beta,
                ActionDomainGate.HumanApproval,
                ToolPredicate(
                    "deployment.promote",
                    ("confirmed", true),
                    ("environment", "production"))),
            Objective(
                gamma,
                ActionDomainGate.HumanApproval,
                ToolPredicate(
                    "deployment.promote",
                    ("confirmed", true),
                    ("environment", "production"))),
            Objective(
                alpha,
                ActionDomainGate.HumanApproval,
                ToolPredicate(
                    "deployment.promote",
                    ("environment", "production"),
                    ("confirmed", true))));
        var firstAuthority = Authority(
            overrides:
            [
                Override(gamma, ActionDomainGate.HumanApproval, "security-lead"),
                Override(alpha, ActionDomainGate.HumanApproval, "ceo"),
                Override(beta, ActionDomainGate.HumanApproval, "ceo"),
            ]);
        var secondAuthority = Authority(
            overrides:
            [
                Override(beta, ActionDomainGate.HumanApproval, "ceo"),
                Override(alpha, ActionDomainGate.HumanApproval, "ceo"),
                Override(gamma, ActionDomainGate.HumanApproval, "security-lead"),
            ]);

        var first = Resolve(firstCatalog, firstFacts, firstAuthority);
        var second = Resolve(secondCatalog, secondFacts, secondAuthority);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(
            ["deployment.alpha", "deployment.beta", "deployment.gamma"],
            first.Matches.Select(match => match.Key.Value));
        Assert.Equal(
            ["ceo", "security-lead"],
            first.RequiredApprovals.Select(requirement => requirement.Approver));
    }

    [Fact]
    public void Result_collections_are_immutable()
    {
        var key = Key("deployment.production");
        var facts = ToolFacts("deployment.promote");
        var resolution = Resolve(
            Catalog(
                Objective(
                    key,
                    ActionDomainGate.HumanApproval,
                    ToolPredicate("deployment.promote"))),
            facts);

        var matches = (IList<ActionGateMatch>)resolution.Matches;
        var approvals = (IList<ActionGateApprovalRequirement>)resolution.RequiredApprovals;
        var approvalKeys = (IList<AuthorityKey>)resolution.RequiredApprovals[0].AuthorityKeys;

        Assert.True(matches.IsReadOnly);
        Assert.True(approvals.IsReadOnly);
        Assert.True(approvalKeys.IsReadOnly);
        Assert.Throws<NotSupportedException>(
            () => matches.Add(
                new ActionGateMatch(
                    Key("deployment.other"),
                    ActionDomainGate.Decide,
                    ActionDomainGate.Decide)));
        Assert.Throws<NotSupportedException>(
            () => approvals.Add(new ActionGateApprovalRequirement(null, [key])));
        Assert.Throws<NotSupportedException>(
            () => approvalKeys.Add(Key("deployment.other")));
    }

    [Fact]
    public void Null_inputs_invalid_default_and_non_scalar_predicates_are_rejected_before_a_decision()
    {
        var catalog = Catalog(Trust(Key("delivery.bug-triage")));
        var authority = Authority();
        var facts = ToolFacts("work.classify");
        var declaration = ActingUnderDeclaration.Missing();

        Assert.Throws<ArgumentNullException>(
            () => ActionGateResolver.Resolve(null!, authority, facts, declaration));
        Assert.Throws<ArgumentNullException>(
            () => ActionGateResolver.Resolve(catalog, null!, facts, declaration));
        Assert.Throws<ArgumentNullException>(
            () => ActionGateResolver.Resolve(catalog, authority, null!, declaration));
        Assert.Throws<ArgumentNullException>(
            () => ActionGateResolver.Resolve(catalog, authority, facts, null!));

        var permissiveDefault = new ActionDomainCatalog(
            version: 1,
            defaults: new ActionDomainCatalogDefaults(ActionDomainGate.Decide),
            domains: [Trust(Key("delivery.other-trust"))]);
        Assert.Throws<ArgumentException>(
            () => ActionGateResolver.Resolve(
                permissiveDefault,
                authority,
                facts,
                declaration));

        var nonScalarPredicate = Catalog(
            Objective(
                Key("delivery.timestamped-action"),
                ActionDomainGate.Escalate,
                ToolPredicate("work.classify", ("observed_at", DateTimeOffset.UtcNow))));
        Assert.Throws<ArgumentException>(
            () => ActionGateResolver.Resolve(
                nonScalarPredicate,
                authority,
                facts,
                declaration));
    }

    [Fact]
    public void Resolver_accepts_only_successful_action_facts_not_extraction_failures()
    {
        var contract = ActionDomainActionContract.ForTool(
            "email.send",
            [
                ActionAttributeDefinition.Direct(
                    "recipient_address",
                    ActionAttributeValueKind.String),
            ]);
        var failed = ActionAttributeExtractorRunner.Extract(
            contract,
            registration: null,
            new ActionAttributeExtractionRequest(
                ActionDomainActionKind.Tool,
                "email.send"));

        Assert.False(failed.IsSuccess);
        Assert.Null(failed.Facts);

        var resolve = typeof(ActionGateResolver).GetMethod(nameof(ActionGateResolver.Resolve));
        Assert.NotNull(resolve);
        var parameters = resolve.GetParameters();
        Assert.Equal(typeof(ActionFacts), parameters[2].ParameterType);
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType == typeof(ActionAttributeExtractionResult));
        Assert.Equal(typeof(ActionGateResolution), resolve.ReturnType);
    }

    private static ActionGateResolution Resolve(
        ActionDomainCatalog catalog,
        ActionFacts facts,
        ActionDomainAuthorityBinding? authority = null) =>
        ActionGateResolver.Resolve(
            catalog,
            authority ?? Authority(),
            facts,
            ActingUnderDeclaration.Missing());

    private static ActionDomainCatalog Catalog(params ActionDomain[] domains) =>
        new(
            version: 1,
            defaults: new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
            domains);

    private static ActionDomain Trust(AuthorityKey key) =>
        new(key, $"Trust domain {key.Value}", ActionDomainGate.Decide, []);

    private static ActionDomain Objective(
        AuthorityKey key,
        ActionDomainGate gate,
        params ActionDomainMatchPredicate[] predicates) =>
        new(key, $"Objective domain {key.Value}", gate, predicates);

    private static ActionDomainMatchPredicate ToolPredicate(
        string tool,
        params (string Name, object Value)[] attributes) =>
        Predicate(ActionDomainActionKind.Tool, "tool", tool, attributes);

    private static ActionDomainMatchPredicate MessagePredicate(
        string messageType,
        params (string Name, object Value)[] attributes) =>
        Predicate(
            ActionDomainActionKind.OrganizationalMessage,
            "message_type",
            messageType,
            attributes);

    private static ActionDomainMatchPredicate Predicate(
        ActionDomainActionKind action,
        string selectorName,
        string selectorValue,
        params (string Name, object Value)[] attributes)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [selectorName] = selectorValue,
        };
        foreach (var (name, value) in attributes)
        {
            values.Add(name, value);
        }

        return new ActionDomainMatchPredicate(action, values);
    }

    private static ActionFacts ToolFacts(
        string tool,
        params (string Name, ActionAttributeValue Value)[] attributes)
    {
        var contract = ActionDomainActionContract.ForTool(
            tool,
            attributes
                .Select(attribute => ActionAttributeDefinition.Direct(
                    attribute.Name,
                    attribute.Value.Kind))
                .ToArray());
        var request = new ActionAttributeExtractionRequest(
            ActionDomainActionKind.Tool,
            tool,
            attributes.ToDictionary(
                attribute => attribute.Name,
                attribute => attribute.Value,
                StringComparer.Ordinal));

        var result = ActionAttributeExtractorRunner.Extract(
            contract,
            registration: null,
            request);

        Assert.True(result.IsSuccess);
        return Assert.IsType<ActionFacts>(result.Facts);
    }

    private static ActionFacts MessageFacts(
        string messageType,
        params (string Name, ActionAttributeValue Value)[] attributes)
    {
        var contract = ActionDomainActionContract.ForOrganizationalMessage(
            messageType,
            attributes
                .Select(attribute => ActionAttributeDefinition.Direct(
                    attribute.Name,
                    attribute.Value.Kind))
                .ToArray());
        var request = new ActionAttributeExtractionRequest(
            ActionDomainActionKind.OrganizationalMessage,
            messageType,
            attributes.ToDictionary(
                attribute => attribute.Name,
                attribute => attribute.Value,
                StringComparer.Ordinal));

        var result = ActionAttributeExtractorRunner.Extract(
            contract,
            registration: null,
            request);

        Assert.True(result.IsSuccess);
        return Assert.IsType<ActionFacts>(result.Facts);
    }

    private static ActionDomainAuthorityBinding Authority(
        IReadOnlyList<AuthorityKey>? canDecide = null,
        IReadOnlyList<ActionDomainAuthorityOverride>? overrides = null) =>
        new("authority", canDecide, overrides);

    private static ActionDomainAuthorityOverride Override(
        AuthorityKey key,
        ActionDomainGate gate,
        string? approver = null) =>
        new(key, gate, approver);

    private static AuthorityKey Key(string value) => AuthorityKey.From(value);
}
