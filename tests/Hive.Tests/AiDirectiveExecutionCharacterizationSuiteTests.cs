using System.Reflection;

namespace Hive.Tests;

internal static class DirectiveExecutionCharacterization
{
    public const string CategoryTrait = "Category";
    public const string Category = "DirectiveExecutionCharacterization";
    public const string ResponsibilityTrait = "DirectiveExecutionResponsibility";

    public const string Recovery = "Recovery";
    public const string Idempotency = "Idempotency";
    public const string Iterations = "Iterations";
    public const string Tools = "Tools";
    public const string Gates = "Gates";
    public const string Outcomes = "Outcomes";
    public const string Audit = "Audit";
    public const string Projections = "Projections";
    public const string PositionEffects = "PositionEffects";

    public static IReadOnlyList<string> RequiredResponsibilities { get; } =
    [
        Recovery,
        Idempotency,
        Iterations,
        Tools,
        Gates,
        Outcomes,
        Audit,
        Projections,
        PositionEffects,
    ];
}

public sealed class AiDirectiveExecutionCharacterizationSuiteTests
{
    [Fact]
    [Trait(
        DirectiveExecutionCharacterization.CategoryTrait,
        DirectiveExecutionCharacterization.Category)]
    public void Suite_covers_every_observable_execution_responsibility()
    {
        var characterizedMethods = typeof(AiDirectiveExecutionCharacterizationSuiteTests)
            .Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic))
            .Where(method => HasTrait(
                method,
                DirectiveExecutionCharacterization.CategoryTrait,
                DirectiveExecutionCharacterization.Category))
            .ToArray();

        Assert.NotEmpty(characterizedMethods);

        var actual = characterizedMethods
            .SelectMany(method => TraitValues(
                method,
                DirectiveExecutionCharacterization.ResponsibilityTrait))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = DirectiveExecutionCharacterization.RequiredResponsibilities
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private static bool HasTrait(MemberInfo member, string name, string value) =>
        TraitValues(member, name).Contains(value, StringComparer.Ordinal);

    private static IEnumerable<string> TraitValues(MemberInfo member, string name) =>
        member.CustomAttributes
            .Where(attribute =>
                attribute.AttributeType == typeof(TraitAttribute) &&
                attribute.ConstructorArguments.Count == 2 &&
                string.Equals(
                    attribute.ConstructorArguments[0].Value as string,
                    name,
                    StringComparison.Ordinal))
            .Select(attribute => attribute.ConstructorArguments[1].Value as string)
            .Where(value => value is not null)
            .Select(value => value!);
}
