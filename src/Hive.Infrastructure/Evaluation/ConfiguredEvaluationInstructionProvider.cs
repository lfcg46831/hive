using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Infrastructure.Evaluation;

internal sealed class ConfiguredEvaluationInstructionProvider : IEvaluationInstructionProvider
{
    private readonly EvaluationProfileCatalog _catalog;

    public ConfiguredEvaluationInstructionProvider(EvaluationProfileCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public EvaluationInstruction? Resolve(
        OrganizationId organizationId,
        PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);

        return _catalog.Resolve(organizationId, positionId)?.Instruction;
    }
}

internal sealed class EvaluationProfileCatalog
{
    private readonly ImmutableDictionary<EvaluationScope, EvaluationProfile> _profiles;

    private EvaluationProfileCatalog(
        ImmutableDictionary<EvaluationScope, EvaluationProfile> profiles)
    {
        _profiles = profiles;
    }

    public int Count => _profiles.Count;

    public static EvaluationProfileCatalog Load(
        EvaluationOptions options,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var builder = ImmutableDictionary.CreateBuilder<EvaluationScope, EvaluationProfile>();
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
            builder.Add(scope, new EvaluationProfile(rubric, rubric.BuildInstruction()));
        }

        return new EvaluationProfileCatalog(builder.ToImmutable());
    }

    public static EvaluationProfileCatalog Single(
        OrganizationId organizationId,
        PositionId positionId,
        EvaluationRubricContract rubric)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(rubric);

        var scope = new EvaluationScope(organizationId.Value, positionId.Value);
        var profile = new EvaluationProfile(rubric, rubric.BuildInstruction());
        return new EvaluationProfileCatalog(
            ImmutableDictionary<EvaluationScope, EvaluationProfile>.Empty.Add(scope, profile));
    }

    public EvaluationProfile? Resolve(
        OrganizationId organizationId,
        PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        return _profiles.GetValueOrDefault(
            new EvaluationScope(organizationId.Value, positionId.Value));
    }
}

internal readonly record struct EvaluationScope(string OrganizationId, string PositionId);

internal sealed record EvaluationProfile(
    EvaluationRubricContract Rubric,
    EvaluationInstruction Instruction);
