using System.Collections.Immutable;
using Hive.Domain.Identity;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Auditing;

/// <summary>
/// Compatibility activation for the explicitly scoped experimental profiles that predate the
/// audit/export boundary. Rubric and scorer settings are deliberately not loaded by the runtime.
/// </summary>
public sealed class DirectiveAuditExportOptions
{
    public const string SectionName = "Hive:Evaluation";

    public Dictionary<string, DirectiveAuditExportProfileOptions> Profiles { get; set; } =
        new(StringComparer.Ordinal);
}

public sealed class DirectiveAuditExportProfileOptions
{
    public bool Enabled { get; set; }

    public string OrganizationId { get; set; } = string.Empty;

    public string PositionId { get; set; } = string.Empty;
}

internal sealed class DirectiveAuditExportOptionsValidator :
    IValidateOptions<DirectiveAuditExportOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        DirectiveAuditExportOptions options)
    {
        try
        {
            _ = DirectiveAuditExportScopeCatalog.Load(options);
            return ValidateOptionsResult.Success;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return ValidateOptionsResult.Fail(
                $"Directive audit/export profile configuration is invalid: {exception.Message}");
        }
    }
}

internal sealed class DirectiveAuditExportScopeCatalog
{
    private readonly ImmutableHashSet<DirectiveAuditExportScope> _scopes;

    private DirectiveAuditExportScopeCatalog(
        ImmutableHashSet<DirectiveAuditExportScope> scopes)
    {
        _scopes = scopes;
    }

    public int Count => _scopes.Count;

    public bool Allows(
        OrganizationId organizationId,
        PositionId positionId) =>
        _scopes.Contains(new DirectiveAuditExportScope(
            organizationId.Value,
            positionId.Value));

    public static DirectiveAuditExportScopeCatalog Load(
        DirectiveAuditExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var profiles = options.Profiles
            ?? throw new InvalidDataException(
                "Directive audit/export profiles collection cannot be null.");
        var builder = ImmutableHashSet.CreateBuilder<DirectiveAuditExportScope>();
        foreach (var (profileName, profile) in profiles
            .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                throw new InvalidDataException(
                    "Directive audit/export profile names must be non-empty.");
            }

            if (profile is null)
            {
                throw new InvalidDataException(
                    $"Directive audit/export profile '{profileName}' configuration is required.");
            }

            if (!profile.Enabled)
            {
                continue;
            }

            var scope = new DirectiveAuditExportScope(
                OrganizationId.From(profile.OrganizationId).Value,
                PositionId.From(profile.PositionId).Value);
            if (!builder.Add(scope))
            {
                throw new InvalidDataException(
                    $"Directive audit/export scope '{scope.OrganizationId}/{scope.PositionId}' is configured more than once.");
            }
        }

        return new DirectiveAuditExportScopeCatalog(builder.ToImmutable());
    }
}

internal readonly record struct DirectiveAuditExportScope(
    string OrganizationId,
    string PositionId);
