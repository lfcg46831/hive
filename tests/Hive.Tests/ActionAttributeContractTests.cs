using Hive.Domain.Governance;

namespace Hive.Tests;

public sealed class ActionAttributeContractTests
{
    [Fact]
    public void Action_attribute_values_use_only_canonical_scalar_kinds()
    {
        var text = ActionAttributeValue.FromString("external");
        var boolean = ActionAttributeValue.FromBoolean(true);
        var integer = ActionAttributeValue.FromInteger(1);
        var number = ActionAttributeValue.FromDecimal(1.00m);

        Assert.Equal(ActionAttributeValueKind.String, text.Kind);
        Assert.Equal("external", text.CanonicalValue);
        Assert.Equal("true", boolean.CanonicalValue);
        Assert.Equal("1", integer.CanonicalValue);
        Assert.Equal("1", number.CanonicalValue);
        Assert.NotEqual(integer, number);
        Assert.True(ActionAttributeValue.TryFromScalar(42, out var parsedInteger));
        Assert.Equal(ActionAttributeValue.FromInteger(42), parsedInteger);
        Assert.True(ActionAttributeValue.TryFromScalar(42.5m, out var parsedDecimal));
        Assert.Equal(ActionAttributeValue.FromDecimal(42.5m), parsedDecimal);
        Assert.False(ActionAttributeValue.TryFromScalar(42.5d, out _));
        Assert.False(ActionAttributeValue.TryFromScalar(new object(), out _));
        Assert.ThrowsAny<ArgumentException>(() => ActionAttributeValue.FromString(" "));
        Assert.ThrowsAny<ArgumentException>(() => ActionAttributeValue.FromString(" external"));
    }

    [Fact]
    public void Action_attribute_definition_requires_structural_names_and_same_typed_allowed_values()
    {
        var allowed = new List<ActionAttributeValue>
        {
            ActionAttributeValue.FromString("internal"),
            ActionAttributeValue.FromString("external"),
        };
        var definition = ActionAttributeDefinition.Derived(
            "recipient_scope",
            ActionAttributeValueKind.String,
            allowed);

        allowed.Clear();

        Assert.Equal(ActionAttributeSource.Derived, definition.Source);
        Assert.Equal(2, definition.AllowedValues.Count);
        Assert.True(definition.Allows(ActionAttributeValue.FromString("external")));
        Assert.False(definition.Allows(ActionAttributeValue.FromString("partner")));
        Assert.ThrowsAny<ArgumentException>(
            () => ActionAttributeDefinition.Direct(
                "recipient scope",
                ActionAttributeValueKind.String));
        Assert.Throws<ArgumentException>(
            () => ActionAttributeDefinition.Derived(
                "recipient_scope",
                ActionAttributeValueKind.String,
                [ActionAttributeValue.FromBoolean(true)]));
        Assert.Throws<ArgumentException>(
            () => ActionAttributeDefinition.Derived(
                "recipient_scope",
                ActionAttributeValueKind.String,
                [
                    ActionAttributeValue.FromString("external"),
                    ActionAttributeValue.FromString("external"),
                ]));
    }

    [Fact]
    public void Action_contract_requires_a_direct_string_selector_and_unique_attributes()
    {
        var contract = ActionDomainActionContract.ForTool(
            "email.send",
            [
                ActionAttributeDefinition.Direct(
                    "recipient_address",
                    ActionAttributeValueKind.String),
                ActionAttributeDefinition.Derived(
                    "recipient_scope",
                    ActionAttributeValueKind.String,
                    [
                        ActionAttributeValue.FromString("internal"),
                        ActionAttributeValue.FromString("external"),
                    ]),
            ]);

        Assert.Equal(ActionDomainActionKind.Tool, contract.Action);
        Assert.Equal("tool", contract.SelectorAttribute);
        Assert.Equal("email.send", contract.SelectorValue);
        Assert.Equal(
            ["tool", "recipient_address", "recipient_scope"],
            contract.ProvidedAttributes);
        Assert.True(contract.HasDerivedAttributes);
        Assert.Equal(ActionAttributeSource.Direct, contract.FindAttribute("tool")!.Source);

        Assert.Throws<ArgumentException>(
            () => ActionDomainActionContract.ForTool(
                "email.send",
                [
                    ActionAttributeDefinition.Direct(
                        "recipient",
                        ActionAttributeValueKind.String),
                    ActionAttributeDefinition.Derived(
                        "recipient",
                        ActionAttributeValueKind.String),
                ]));
        Assert.Throws<ArgumentException>(
            () => new ActionDomainActionContract(
                ActionDomainActionKind.Tool,
                "message_type",
                "email.send",
                [
                    ActionAttributeDefinition.Direct(
                        "message_type",
                        ActionAttributeValueKind.String,
                        [ActionAttributeValue.FromString("email.send")]),
                ]));
        Assert.Throws<ArgumentException>(
            () => new ActionDomainActionContract(
                ActionDomainActionKind.Tool,
                "tool",
                "email.send",
                [
                    ActionAttributeDefinition.Derived(
                        "tool",
                        ActionAttributeValueKind.String,
                        [ActionAttributeValue.FromString("email.send")]),
                ]));
    }

    [Fact]
    public void Action_contract_and_extraction_request_snapshot_inputs()
    {
        var definitions = new List<ActionAttributeDefinition>
        {
            ActionAttributeDefinition.Direct(
                "recipient_address",
                ActionAttributeValueKind.String),
        };
        var contract = ActionDomainActionContract.ForTool("email.send", definitions);
        var direct = new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
        {
            ["recipient_address"] = ActionAttributeValue.FromString("person@example.test"),
        };
        var request = new ActionAttributeExtractionRequest(
            ActionDomainActionKind.Tool,
            "email.send",
            direct);

        definitions.Clear();
        direct.Clear();

        Assert.Equal(2, contract.Attributes.Count);
        Assert.Single(request.DirectAttributes);
        Assert.Equal(
            "person@example.test",
            request.DirectAttributes["recipient_address"].CanonicalValue);
    }
}
