using System.Collections.Immutable;
using Hive.Domain.Governance;

namespace Hive.Tests;

public sealed class ActionAttributeExtractorContractTests
{
    [Fact]
    public void Reference_extractor_is_deterministic_for_equal_action_and_configuration_snapshots()
    {
        var contract = EmailContract();
        var internalDomains = new List<string> { "hive.test" };
        var firstRegistration = ActionAttributeExtractorRegistration.ForTool(
            "email.send",
            new RecipientScopeExtractor(internalDomains));
        var secondRegistration = ActionAttributeExtractorRegistration.ForTool(
            "email.send",
            new RecipientScopeExtractor(["hive.test"]));
        var firstRequest = Request(
            ("sender_address", "agent@hive.test"),
            ("recipient_address", "person@hive.test"));
        var secondRequest = Request(
            ("recipient_address", "person@hive.test"),
            ("sender_address", "agent@hive.test"));
        internalDomains.Clear();
        internalDomains.Add("example.test");

        var first = ActionAttributeExtractorRunner.Extract(
            contract,
            firstRegistration,
            firstRequest);
        var repeated = ActionAttributeExtractorRunner.Extract(
            contract,
            firstRegistration,
            firstRequest);
        var equivalent = ActionAttributeExtractorRunner.Extract(
            contract,
            secondRegistration,
            secondRequest);

        Assert.True(first.IsSuccess);
        Assert.True(
            ActionAttributeExtractorContractVerifier.IsDeterministic(
                contract,
                firstRegistration,
                firstRequest));
        Assert.True(ActionAttributeExtractorContractVerifier.AreEquivalent(first, repeated));
        Assert.True(ActionAttributeExtractorContractVerifier.AreEquivalent(first, equivalent));
        Assert.Equal(
            "internal",
            first.Facts!.Attributes["recipient_scope"].CanonicalValue);
    }

    [Fact]
    public void Reference_extractor_does_not_mutate_direct_facts_and_returns_immutable_facts()
    {
        var mutableOutput = new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
        {
            ["recipient_scope"] = ActionAttributeValue.FromString("external"),
        };
        var registration = ActionAttributeExtractorRegistration.ForTool(
            "email.send",
            new DelegateExtractor(_ => ActionAttributeExtractorOutput.Success(mutableOutput)));
        var source = new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
        {
            ["recipient_address"] = ActionAttributeValue.FromString("customer@example.test"),
            ["sender_address"] = ActionAttributeValue.FromString("agent@hive.test"),
        };
        var request = new ActionAttributeExtractionRequest(
            ActionDomainActionKind.Tool,
            "email.send",
            source);

        var result = ActionAttributeExtractorRunner.Extract(EmailContract(), registration, request);
        source["recipient_address"] = ActionAttributeValue.FromString("changed@hive.test");
        mutableOutput["recipient_scope"] = ActionAttributeValue.FromString("internal");

        Assert.True(result.IsSuccess);
        Assert.Equal(
            "customer@example.test",
            result.Facts!.Attributes["recipient_address"].CanonicalValue);
        Assert.Equal(
            "external",
            result.Facts.Attributes["recipient_scope"].CanonicalValue);
    }

    [Fact]
    public void Determinism_verifier_rejects_a_stateful_extractor()
    {
        var extractor = new AlternatingExtractor();
        var registration = ActionAttributeExtractorRegistration.ForTool("email.send", extractor);
        var request = Request(
            ("recipient_address", "customer@example.test"),
            ("sender_address", "agent@hive.test"));

        Assert.False(
            ActionAttributeExtractorContractVerifier.IsDeterministic(
                EmailContract(),
                registration,
                request));
    }

    [Fact]
    public void Invalid_success_outputs_fail_closed_without_partial_facts()
    {
        var cases = new (IActionAttributeExtractor Extractor, string Code)[]
        {
            (
                new DelegateExtractor(_ => ActionAttributeExtractorOutput.Success()),
                "derived-attribute-missing"),
            (
                Derived("unexpected", ActionAttributeValue.FromString("external")),
                "derived-attribute-unexpected"),
            (
                Derived("recipient_scope", ActionAttributeValue.FromBoolean(true)),
                "derived-attribute-type-mismatch"),
            (
                Derived("recipient_scope", ActionAttributeValue.FromString("partner")),
                "derived-attribute-value-not-allowed"),
            (
                Derived("recipient_address", ActionAttributeValue.FromString("changed@hive.test")),
                "derived-direct-attribute-collision"),
        };
        var request = Request(
            ("recipient_address", "customer@example.test"),
            ("sender_address", "agent@hive.test"));

        foreach (var (extractor, code) in cases)
        {
            var result = ActionAttributeExtractorRunner.Extract(
                EmailContract(),
                ActionAttributeExtractorRegistration.ForTool("email.send", extractor),
                request);

            Assert.False(result.IsSuccess);
            Assert.Null(result.Facts);
            Assert.Equal(ActionAttributeExtractionFailureKind.ContractViolation, result.Failure!.Kind);
            Assert.Equal(code, result.Failure.Code);
        }
    }

    [Fact]
    public void Direct_facts_must_be_total_declared_and_cannot_claim_derived_values()
    {
        var registration = ActionAttributeExtractorRegistration.ForTool(
            "email.send",
            new RecipientScopeExtractor(["hive.test"]));
        var missing = Request(("recipient_address", "customer@example.test"));
        var derivedCollision = Request(
            ("recipient_address", "customer@example.test"),
            ("sender_address", "agent@hive.test"),
            ("recipient_scope", "external"));

        var missingResult = ActionAttributeExtractorRunner.Extract(
            EmailContract(),
            registration,
            missing);
        var collisionResult = ActionAttributeExtractorRunner.Extract(
            EmailContract(),
            registration,
            derivedCollision);

        Assert.Equal("direct-attribute-missing", missingResult.Failure!.Code);
        Assert.Null(missingResult.Facts);
        Assert.Equal("direct-derived-attribute-collision", collisionResult.Failure!.Code);
        Assert.Null(collisionResult.Facts);
    }

    [Fact]
    public void Runtime_boundary_rejects_mismatches_and_invalid_direct_facts_before_connector_code()
    {
        var contract = EmailContract();
        var counting = new CountingExtractor();
        var registration = ActionAttributeExtractorRegistration.ForTool("email.send", counting);
        var valid = Request(
            ("recipient_address", "customer@example.test"),
            ("sender_address", "agent@hive.test"));
        var wrongSelector = new ActionAttributeExtractionRequest(
            ActionDomainActionKind.Tool,
            "email.other",
            valid.DirectAttributes);
        var selectorCollision = Request(
            ("tool", "email.send"),
            ("recipient_address", "customer@example.test"),
            ("sender_address", "agent@hive.test"));
        var unknown = Request(
            ("secret@example.test", "value"),
            ("recipient_address", "customer@example.test"),
            ("sender_address", "agent@hive.test"));
        var wrongType = new ActionAttributeExtractionRequest(
            ActionDomainActionKind.Tool,
            "email.send",
            new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
            {
                ["recipient_address"] = ActionAttributeValue.FromBoolean(true),
                ["sender_address"] = ActionAttributeValue.FromString("agent@hive.test"),
            });
        var disallowed = Request(
            ("recipient_address", "customer@example.test"),
            ("sender_address", "other@hive.test"));

        var results = new[]
        {
            ActionAttributeExtractorRunner.Extract(contract, registration, wrongSelector),
            ActionAttributeExtractorRunner.Extract(contract, registration, selectorCollision),
            ActionAttributeExtractorRunner.Extract(contract, registration, unknown),
            ActionAttributeExtractorRunner.Extract(contract, registration, wrongType),
            ActionAttributeExtractorRunner.Extract(contract, registration, disallowed),
        };

        Assert.Equal(
            [
                "action-contract-mismatch",
                "direct-attribute-selector-collision",
                "direct-attribute-not-declared",
                "direct-attribute-type-mismatch",
                "direct-attribute-value-not-allowed",
            ],
            results.Select(result => result.Failure!.Code).ToArray());
        Assert.All(results, result => Assert.Null(result.Facts));
        Assert.Null(results[2].Failure!.Attribute);
        Assert.Equal(0, counting.CallCount);
    }

    [Fact]
    public void Runtime_boundary_rejects_missing_unexpected_mismatched_and_null_extractors()
    {
        var contract = EmailContract();
        var valid = Request(
            ("recipient_address", "customer@example.test"),
            ("sender_address", "agent@hive.test"));
        var directOnly = ActionDomainActionContract.ForTool(
            "http.get",
            [ActionAttributeDefinition.Direct("url", ActionAttributeValueKind.String)]);
        var directOnlyRequest = new ActionAttributeExtractionRequest(
            ActionDomainActionKind.Tool,
            "http.get",
            new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
            {
                ["url"] = ActionAttributeValue.FromString("https://example.test"),
            });

        var missing = ActionAttributeExtractorRunner.Extract(contract, null, valid);
        var mismatched = ActionAttributeExtractorRunner.Extract(
            contract,
            ActionAttributeExtractorRegistration.ForTool("email.other", new CountingExtractor()),
            valid);
        var unexpected = ActionAttributeExtractorRunner.Extract(
            directOnly,
            ActionAttributeExtractorRegistration.ForTool("http.get", new CountingExtractor()),
            directOnlyRequest);
        var returnedNull = ActionAttributeExtractorRunner.Extract(
            contract,
            ActionAttributeExtractorRegistration.ForTool("email.send", new NullExtractor()),
            valid);

        Assert.Equal("action-extractor-missing", missing.Failure!.Code);
        Assert.Equal("action-extractor-contract-mismatch", mismatched.Failure!.Code);
        Assert.Equal("action-extractor-unexpected", unexpected.Failure!.Code);
        Assert.Equal("action-attribute-extractor-returned-null", returnedNull.Failure!.Code);
        Assert.Null(missing.Facts);
        Assert.Null(mismatched.Facts);
        Assert.Null(unexpected.Facts);
        Assert.Null(returnedNull.Facts);
    }

    [Fact]
    public void Returned_failures_and_exceptions_are_sanitized_without_partial_facts()
    {
        var request = Request(
            ("recipient_address", "customer@example.test"),
            ("sender_address", "agent@hive.test"));
        var rejected = ActionAttributeExtractorRunner.Extract(
            EmailContract(),
            ActionAttributeExtractorRegistration.ForTool(
                "email.send",
                new DelegateExtractor(
                    _ => ActionAttributeExtractorOutput.Failure(
                        ActionAttributeExtractorFailureReason.InvalidInput))),
            request);
        var threw = ActionAttributeExtractorRunner.Extract(
            EmailContract(),
            ActionAttributeExtractorRegistration.ForTool(
                "email.send",
                new DelegateExtractor(
                    _ => throw new InvalidOperationException("secret@example.test"))),
            request);

        Assert.False(rejected.IsSuccess);
        Assert.Null(rejected.Facts);
        Assert.Equal(ActionAttributeExtractionFailureKind.ExtractorRejected, rejected.Failure!.Kind);
        Assert.Equal("action-attribute-extractor-invalid-input", rejected.Failure.Code);
        Assert.False(threw.IsSuccess);
        Assert.Null(threw.Facts);
        Assert.Equal(ActionAttributeExtractionFailureKind.ExtractorThrew, threw.Failure!.Kind);
        Assert.Equal("action-attribute-extractor-threw", threw.Failure.Code);
        Assert.DoesNotContain("secret", threw.Failure.Code, StringComparison.Ordinal);
    }

    private static ActionDomainActionContract EmailContract() =>
        ActionDomainActionContract.ForTool(
            "email.send",
            [
                ActionAttributeDefinition.Direct(
                    "recipient_address",
                    ActionAttributeValueKind.String),
                ActionAttributeDefinition.Direct(
                    "sender_address",
                    ActionAttributeValueKind.String,
                    [ActionAttributeValue.FromString("agent@hive.test")]),
                ActionAttributeDefinition.Derived(
                    "recipient_scope",
                    ActionAttributeValueKind.String,
                    [
                        ActionAttributeValue.FromString("internal"),
                        ActionAttributeValue.FromString("external"),
                    ]),
            ]);

    private static ActionAttributeExtractionRequest Request(
        params (string Name, string Value)[] attributes) =>
        new(
            ActionDomainActionKind.Tool,
            "email.send",
            attributes.ToDictionary(
                pair => pair.Name,
                pair => ActionAttributeValue.FromString(pair.Value),
                StringComparer.Ordinal));

    private static DelegateExtractor Derived(string name, ActionAttributeValue value) =>
        new(
            _ => ActionAttributeExtractorOutput.Success(
                new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
                {
                    [name] = value,
                }));

    private sealed class RecipientScopeExtractor : IActionAttributeExtractor
    {
        private readonly ImmutableHashSet<string> _internalDomains;

        public RecipientScopeExtractor(IEnumerable<string> internalDomains)
        {
            _internalDomains = internalDomains.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public ActionAttributeExtractorOutput Extract(ActionAttributeExtractionRequest request)
        {
            var recipient = request.DirectAttributes["recipient_address"].CanonicalValue;
            var separator = recipient.LastIndexOf('@');
            if (separator <= 0 || separator == recipient.Length - 1)
            {
                return ActionAttributeExtractorOutput.Failure(
                    ActionAttributeExtractorFailureReason.InvalidInput);
            }

            var domain = recipient[(separator + 1)..];
            var scope = _internalDomains.Contains(domain) ? "internal" : "external";
            return ActionAttributeExtractorOutput.Success(
                new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
                {
                    ["recipient_scope"] = ActionAttributeValue.FromString(scope),
                });
        }
    }

    private sealed class DelegateExtractor : IActionAttributeExtractor
    {
        private readonly Func<ActionAttributeExtractionRequest, ActionAttributeExtractorOutput> _extract;

        public DelegateExtractor(
            Func<ActionAttributeExtractionRequest, ActionAttributeExtractorOutput> extract)
        {
            _extract = extract;
        }

        public ActionAttributeExtractorOutput Extract(ActionAttributeExtractionRequest request) =>
            _extract(request);
    }

    private sealed class AlternatingExtractor : IActionAttributeExtractor
    {
        private bool _external;

        public ActionAttributeExtractorOutput Extract(ActionAttributeExtractionRequest request)
        {
            _external = !_external;
            return ActionAttributeExtractorOutput.Success(
                new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
                {
                    ["recipient_scope"] = ActionAttributeValue.FromString(
                        _external ? "external" : "internal"),
                });
        }
    }

    private sealed class CountingExtractor : IActionAttributeExtractor
    {
        public int CallCount { get; private set; }

        public ActionAttributeExtractorOutput Extract(ActionAttributeExtractionRequest request)
        {
            CallCount++;
            return ActionAttributeExtractorOutput.Success(
                new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
                {
                    ["recipient_scope"] = ActionAttributeValue.FromString("external"),
                });
        }
    }

    private sealed class NullExtractor : IActionAttributeExtractor
    {
        public ActionAttributeExtractorOutput Extract(ActionAttributeExtractionRequest request) =>
            null!;
    }
}

internal static class ActionAttributeExtractorContractVerifier
{
    public static bool IsDeterministic(
        ActionDomainActionContract contract,
        ActionAttributeExtractorRegistration registration,
        ActionAttributeExtractionRequest request)
    {
        var first = ActionAttributeExtractorRunner.Extract(contract, registration, request);
        var second = ActionAttributeExtractorRunner.Extract(contract, registration, request);

        return AreEquivalent(first, second);
    }

    public static bool AreEquivalent(
        ActionAttributeExtractionResult first,
        ActionAttributeExtractionResult second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.IsSuccess != second.IsSuccess)
        {
            return false;
        }

        if (!first.IsSuccess)
        {
            return first.Failure == second.Failure;
        }

        return first.Facts!.Action == second.Facts!.Action
               && string.Equals(
                   first.Facts.SelectorValue,
                   second.Facts.SelectorValue,
                   StringComparison.Ordinal)
               && first.Facts.Attributes.SequenceEqual(second.Facts.Attributes);
    }
}
