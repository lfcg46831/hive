using System.Collections.Immutable;
using Hive.Domain.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Evaluation;

internal sealed class ConfiguredEvaluationInstructionProvider : IEvaluationInstructionProvider
{
    private readonly ImmutableDictionary<EvaluationScope, EvaluationInstruction> _instructions;

    public ConfiguredEvaluationInstructionProvider(
        IOptions<EvaluationOptions> options,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        _instructions = EvaluationProfileCatalog.Load(options.Value, environment.ContentRootPath);
    }

    public EvaluationInstruction? Resolve(
        OrganizationId organizationId,
        PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);

        return _instructions.GetValueOrDefault(
            new EvaluationScope(organizationId.Value, positionId.Value));
    }
}

internal static class EvaluationProfileCatalog
{
    public static ImmutableDictionary<EvaluationScope, EvaluationInstruction> Load(
        EvaluationOptions options,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var builder = ImmutableDictionary.CreateBuilder<EvaluationScope, EvaluationInstruction>();
        var profiles = options.Profiles
            ?? throw new InvalidDataException("Evaluation profiles collection cannot be null.");
        foreach (var (profileName, profile) in profiles
            .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                throw new InvalidDataException("Evaluation profile names must be non-empty.");
            }

            if (profile is null)
            {
                throw new InvalidDataException(
                    $"Evaluation profile '{profileName}' configuration is required.");
            }

            if (!profile.Enabled)
            {
                continue;
            }

            var organization = OrganizationId.From(profile.OrganizationId);
            var position = PositionId.From(profile.PositionId);
            var scope = new EvaluationScope(organization.Value, position.Value);
            if (builder.ContainsKey(scope))
            {
                throw new InvalidDataException(
                    $"Evaluation scope '{organization}/{position}' is configured by more than one enabled profile.");
            }

            var rubricPath = profile.ResolveRubricPath(contentRootPath, profileName);
            var rubric = EvaluationRubricContract.Load(rubricPath, profile.RubricVersion);
            builder.Add(scope, rubric.BuildInstruction());
        }

        return builder.ToImmutable();
    }
}

internal readonly record struct EvaluationScope(string OrganizationId, string PositionId);
