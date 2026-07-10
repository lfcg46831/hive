using Hive.Domain.Governance;

namespace Hive.Tests;

public sealed class ActingUnderDeclarationTests
{
    [Fact]
    public void State_vocabulary_is_closed()
    {
        Assert.Equal(
            ["Declared", "Missing", "Invalid"],
            Enum.GetNames<ActingUnderDeclarationState>());
    }

    [Fact]
    public void Factories_preserve_state_key_and_stable_code_invariants()
    {
        var key = AuthorityKey.From("delivery.bug-triage");

        var declared = ActingUnderDeclaration.Declared(key);
        var missing = ActingUnderDeclaration.Missing();
        var invalid = ActingUnderDeclaration.Invalid();

        Assert.Equal(ActingUnderDeclarationState.Declared, declared.State);
        Assert.Same(key, declared.Key);
        Assert.Equal(ActingUnderDeclaration.DeclaredCode, declared.Code);
        Assert.Equal("acting-under-declared", declared.Code);

        Assert.Equal(ActingUnderDeclarationState.Missing, missing.State);
        Assert.Null(missing.Key);
        Assert.Equal(ActingUnderDeclaration.MissingCode, missing.Code);
        Assert.Equal("acting-under-missing", missing.Code);

        Assert.Equal(ActingUnderDeclarationState.Invalid, invalid.State);
        Assert.Null(invalid.Key);
        Assert.Equal(ActingUnderDeclaration.InvalidCode, invalid.Code);
        Assert.Equal("acting-under-invalid", invalid.Code);
    }

    [Fact]
    public void Declared_factory_rejects_a_missing_key()
    {
        Assert.Throws<ArgumentNullException>(() => ActingUnderDeclaration.Declared(null!));
    }

    [Fact]
    public void Resolver_treats_an_absent_field_as_missing()
    {
        var declaration = ActingUnderDeclaration.Resolve(
            fieldPresent: false,
            value: null,
            allowedKeys: [AuthorityKey.From("delivery.bug-triage")]);

        Assert.Equal(ActingUnderDeclarationState.Missing, declaration.State);
        Assert.Null(declaration.Key);
        Assert.Equal("acting-under-missing", declaration.Code);
    }

    [Fact]
    public void Resolver_preserves_the_canonical_allowed_key_for_an_exact_match()
    {
        var allowed = AuthorityKey.From("delivery.bug-triage");

        var declaration = ActingUnderDeclaration.Resolve(
            fieldPresent: true,
            value: "delivery.bug-triage",
            allowedKeys: [allowed]);

        Assert.Equal(ActingUnderDeclarationState.Declared, declaration.State);
        Assert.Same(allowed, declaration.Key);
        Assert.Equal("acting-under-declared", declaration.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("delivery")]
    [InlineData(".bug-triage")]
    [InlineData("delivery.")]
    [InlineData("delivery..bug-triage")]
    public void Resolver_treats_a_present_null_empty_or_malformed_value_as_invalid(string? value)
    {
        var declaration = ActingUnderDeclaration.Resolve(
            fieldPresent: true,
            value,
            allowedKeys: [AuthorityKey.From("delivery.bug-triage")]);

        Assert.Equal(ActingUnderDeclarationState.Invalid, declaration.State);
        Assert.Null(declaration.Key);
        Assert.Equal("acting-under-invalid", declaration.Code);
    }

    [Theory]
    [InlineData("Delivery.bug-triage")]
    [InlineData("delivery.Bug-triage")]
    [InlineData("finance.commitments")]
    public void Resolver_is_case_sensitive_and_rejects_values_outside_the_allowlist(string value)
    {
        var declaration = ActingUnderDeclaration.Resolve(
            fieldPresent: true,
            value,
            allowedKeys: [AuthorityKey.From("delivery.bug-triage")]);

        Assert.Equal(ActingUnderDeclarationState.Invalid, declaration.State);
        Assert.Null(declaration.Key);
        Assert.Equal("acting-under-invalid", declaration.Code);
    }

    [Fact]
    public void Invalid_declaration_does_not_retain_the_raw_value()
    {
        const string rawValue = "finance.secret-override";

        var declaration = ActingUnderDeclaration.Resolve(
            fieldPresent: true,
            value: rawValue,
            allowedKeys: [AuthorityKey.From("delivery.bug-triage")]);

        Assert.Null(declaration.Key);
        Assert.DoesNotContain(rawValue, declaration.ToString());
    }

    [Fact]
    public void Resolver_rejects_an_invalid_allowlist()
    {
        Assert.Throws<ArgumentNullException>(
            () => ActingUnderDeclaration.Resolve(fieldPresent: false, value: null, allowedKeys: null!));
        Assert.Throws<ArgumentException>(
            () => ActingUnderDeclaration.Resolve(
                fieldPresent: false,
                value: null,
                allowedKeys: new AuthorityKey[] { null! }));
    }
}
