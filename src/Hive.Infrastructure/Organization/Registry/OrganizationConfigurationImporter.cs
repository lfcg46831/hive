using System.Collections.ObjectModel;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Organization.Configuration.Validation;

namespace Hive.Infrastructure.Organization.Registry;

public sealed class OrganizationConfigurationImporter
{
    private readonly InMemoryOrganizationRegistry _registry;
    private readonly TimeProvider _timeProvider;

    public OrganizationConfigurationImporter(
        InMemoryOrganizationRegistry registry,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public OrganizationImportResult Import(OrganizationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var validation = Validate(configuration);
        if (!validation.IsValid)
        {
            return Invalid(validation.Errors);
        }

        OrganizationRegistryProjection target;
        try
        {
            target = OrganizationRegistryProjection.Create(configuration);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            return Invalid(
            [
                new OrganizationConfigurationValidationError(
                    "command-relations-invalid",
                    "positions[].reports_to",
                    exception.Message),
            ]);
        }

        return _registry.Mutate(
            target.OrganizationId,
            current =>
            {
                if (current?.Fingerprint == target.Fingerprint)
                {
                    var noChanges = new OrganizationImportPlan(
                        target.OrganizationId,
                        target.Fingerprint,
                        current.Version,
                        Array.Empty<OrganizationRegistryChange>());
                    return (
                        current,
                        new OrganizationImportResult(
                            OrganizationImportStatus.NoChanges,
                            noChanges,
                            current));
                }

                var changes = Changes(current, target);
                var nextVersion = (current?.Version ?? 0) + 1;
                var plan = new OrganizationImportPlan(
                    target.OrganizationId,
                    target.Fingerprint,
                    nextVersion,
                    Array.AsReadOnly(changes.ToArray()));
                var now = _timeProvider.GetUtcNow();
                var snapshot = new OrganizationRegistrySnapshot(
                    target.OrganizationId,
                    nextVersion,
                    target.Fingerprint,
                    now,
                    Materialize(current?.Organization, target.Organization, now),
                    Materialize(current?.Units, target.Units, now),
                    Materialize(current?.Positions, target.Positions, now),
                    Materialize(current?.Occupants, target.Occupants, now),
                    Materialize(current?.Authorities, target.Authorities, now),
                    Materialize(current?.Schedules, target.Schedules, now),
                    Materialize(current?.Relations, target.Relations, now));

                return (
                    snapshot,
                    new OrganizationImportResult(OrganizationImportStatus.Applied, plan, snapshot));
            });
    }

    private static OrganizationConfigurationValidationResult Validate(
        OrganizationConfiguration configuration) =>
        OrganizationConfigurationValidationResult.Create(
            OrganizationConfigurationUniquenessValidator.Validate(configuration).Errors
                .Concat(OrganizationConfigurationCrossReferenceValidator.Validate(configuration).Errors)
                .Concat(OrganizationConfigurationStructuralValidator.Validate(configuration).Errors));

    private static OrganizationImportResult Invalid(
        IEnumerable<OrganizationConfigurationValidationError> errors)
    {
        var validation = OrganizationConfigurationValidationResult.Create(errors);
        return new OrganizationImportResult(
            OrganizationImportStatus.Invalid,
            plan: null,
            snapshot: null,
            validation.Errors);
    }

    private static List<OrganizationRegistryChange> Changes(
        OrganizationRegistrySnapshot? current,
        OrganizationRegistryProjection target)
    {
        var changes = new List<OrganizationRegistryChange>();
        AddSingleChange(
            changes,
            RegistryEntityKind.Organization,
            target.OrganizationId.Value,
            current?.Organization,
            target.Organization);
        AddDictionaryChanges(
            changes,
            RegistryEntityKind.Unit,
            current?.Units,
            target.Units,
            key => key.Value);
        AddDictionaryChanges(
            changes,
            RegistryEntityKind.Position,
            current?.Positions,
            target.Positions,
            key => key.Value);
        AddDictionaryChanges(
            changes,
            RegistryEntityKind.Occupant,
            current?.Occupants,
            target.Occupants,
            key => key.Value);
        AddDictionaryChanges(
            changes,
            RegistryEntityKind.Authority,
            current?.Authorities,
            target.Authorities,
            key => key.Value);
        AddDictionaryChanges(
            changes,
            RegistryEntityKind.Schedule,
            current?.Schedules,
            target.Schedules,
            key => key.ToString());
        AddSingleChange(
            changes,
            RegistryEntityKind.CommandRelations,
            target.OrganizationId.Value,
            current?.Relations,
            target.Relations);

        return changes
            .OrderBy(change => change.EntityKind)
            .ThenBy(change => change.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddSingleChange<T>(
        List<OrganizationRegistryChange> changes,
        RegistryEntityKind entityKind,
        string key,
        RegistryEntry<T>? current,
        OrganizationRegistryProjection.ProjectedEntry<T> target)
    {
        if (current is null)
        {
            changes.Add(new OrganizationRegistryChange(entityKind, key, RegistryChangeKind.Added));
        }
        else if (!string.Equals(current.Fingerprint, target.Fingerprint, StringComparison.Ordinal))
        {
            changes.Add(new OrganizationRegistryChange(entityKind, key, RegistryChangeKind.Updated));
        }
    }

    private static void AddDictionaryChanges<TKey, TValue>(
        List<OrganizationRegistryChange> changes,
        RegistryEntityKind entityKind,
        IReadOnlyDictionary<TKey, RegistryEntry<TValue>>? current,
        IReadOnlyDictionary<TKey, OrganizationRegistryProjection.ProjectedEntry<TValue>> target,
        Func<TKey, string> keyText)
        where TKey : notnull
    {
        foreach (var (key, targetEntry) in target)
        {
            if (current is null || !current.TryGetValue(key, out var currentEntry))
            {
                changes.Add(new OrganizationRegistryChange(
                    entityKind,
                    keyText(key),
                    RegistryChangeKind.Added));
            }
            else if (!string.Equals(
                currentEntry.Fingerprint,
                targetEntry.Fingerprint,
                StringComparison.Ordinal))
            {
                changes.Add(new OrganizationRegistryChange(
                    entityKind,
                    keyText(key),
                    RegistryChangeKind.Updated));
            }
        }

        if (current is null)
        {
            return;
        }

        foreach (var key in current.Keys.Where(key => !target.ContainsKey(key)))
        {
            changes.Add(new OrganizationRegistryChange(
                entityKind,
                keyText(key),
                RegistryChangeKind.Removed));
        }
    }

    private static RegistryEntry<T> Materialize<T>(
        RegistryEntry<T>? current,
        OrganizationRegistryProjection.ProjectedEntry<T> target,
        DateTimeOffset now) =>
        current is not null
        && string.Equals(current.Fingerprint, target.Fingerprint, StringComparison.Ordinal)
            ? current
            : new RegistryEntry<T>(target.Value, target.Fingerprint, now);

    private static IReadOnlyDictionary<TKey, RegistryEntry<TValue>> Materialize<TKey, TValue>(
        IReadOnlyDictionary<TKey, RegistryEntry<TValue>>? current,
        IReadOnlyDictionary<TKey, OrganizationRegistryProjection.ProjectedEntry<TValue>> target,
        DateTimeOffset now)
        where TKey : notnull =>
        new ReadOnlyDictionary<TKey, RegistryEntry<TValue>>(
            target.ToDictionary(
                pair => pair.Key,
                pair => Materialize(
                    current is not null && current.TryGetValue(pair.Key, out var entry) ? entry : null,
                    pair.Value,
                    now)));
}
