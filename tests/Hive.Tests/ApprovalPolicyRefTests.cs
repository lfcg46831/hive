using System.Reflection;
using Hive.Domain.Identity;

namespace Hive.Tests;

public sealed class ApprovalPolicyRefTests
{
    [Fact]
    public void From_preserves_valid_policy_key()
    {
        var policy = ApprovalPolicyRef.From("production-release");

        Assert.Equal("production-release", policy.Value);
        Assert.Equal("production-release", policy.ToString());
    }

    [Fact]
    public void Policy_references_compare_by_ordinal_value()
    {
        Assert.Equal(
            ApprovalPolicyRef.From("production-release"),
            ApprovalPolicyRef.From("production-release"));
        Assert.NotEqual(
            ApprovalPolicyRef.From("production-release"),
            ApprovalPolicyRef.From("Production-Release"));
    }

    [Fact]
    public void From_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => ApprovalPolicyRef.From(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" production-release")]
    [InlineData("production-release ")]
    public void From_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => ApprovalPolicyRef.From(value));
    }

    [Fact]
    public void Policy_reference_exposes_no_implicit_conversions()
    {
        Assert.DoesNotContain(
            typeof(ApprovalPolicyRef).GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.Name == "op_Implicit");
    }
}
