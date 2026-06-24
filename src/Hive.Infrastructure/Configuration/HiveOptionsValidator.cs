using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Configuration;

public sealed class HiveOptionsValidator : IValidateOptions<HiveOptions>
{
    private const string RolesPath = $"{HiveOptions.SectionName}:Node:Roles";
    private const string NumberOfShardsPath = $"{HiveOptions.SectionName}:Agents:NumberOfShards";

    private static readonly HashSet<string> KnownRoles =
        new(NodeRoleNames.All, StringComparer.OrdinalIgnoreCase);

    public ValidateOptionsResult Validate(string? name, HiveOptions options)
    {
        var roles = options.Node?.Roles;
        if (roles is null || roles.Length == 0)
        {
            return ValidateOptionsResult.Fail($"{RolesPath} must contain at least one role.");
        }

        var failures = new List<string>();
        var comparableRoles = new List<(string Raw, string Trimmed)>();

        for (var index = 0; index < roles.Length; index++)
        {
            var role = roles[index];
            if (string.IsNullOrWhiteSpace(role))
            {
                failures.Add(
                    $"{RolesPath}[{index}] must not be empty (configured value: {Format(role)}).");
                continue;
            }

            var trimmed = role.Trim();
            comparableRoles.Add((role, trimmed));

            if (!KnownRoles.Contains(trimmed))
            {
                failures.Add(
                    $"{RolesPath}[{index}] contains unknown role {Format(role)}. " +
                    $"Allowed values: {string.Join(", ", NodeRoleNames.All)}.");
            }
        }

        foreach (var duplicateGroup in comparableRoles
                     .GroupBy(role => role.Trimmed, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            failures.Add(
                $"{RolesPath} contains duplicate role values after trimming and " +
                $"case-insensitive comparison: {string.Join(", ", duplicateGroup.Select(role => Format(role.Raw)))}.");
        }

        if (options.Agents?.NumberOfShards is { } numberOfShards && numberOfShards <= 0)
        {
            failures.Add(
                $"{NumberOfShardsPath} must be greater than zero when set " +
                $"(configured value: {numberOfShards}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static string Format(string? value)
    {
        if (value is null)
        {
            return "<null>";
        }

        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return $"<whitespace:{value.Length}>";
        }

        return $"\"{value.Replace("\"", "\\\"")}\"";
    }
}
