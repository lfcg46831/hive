using System.Collections.ObjectModel;

namespace Hive.Infrastructure.Organization.Registry;

internal static class OrganizationRegistryMutation
{
    public static OrganizationImportResult Apply(
        OrganizationRegistrySnapshot? current,
        OrganizationRegistryProjection target,
        DateTimeOffset importedAt)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (current?.Fingerprint == target.Fingerprint)
        {
            var noChanges = new OrganizationImportPlan(
                target.OrganizationId,
                target.Fingerprint,
                current.Version,
                Array.Empty<OrganizationRegistryChange>());
            return new OrganizationImportResult(
                OrganizationImportStatus.NoChanges,
                noChanges,
                current);
        }

        var changes = Changes(current, target);
        var nextVersion = (current?.Version ?? 0) + 1;
        var plan = new OrganizationImportPlan(
            target.OrganizationId,
            target.Fingerprint,
            nextVersion,
            Array.AsReadOnly(changes.ToArray()));
        var snapshot = new OrganizationRegistrySnapshot(
            target.OrganizationId,
            nextVersion,
            target.Fingerprint,
            importedAt,
            Materialize(current?.Organization, target.Organization, importedAt),
            Materialize(current?.Units, target.Units, importedAt),
            Materialize(current?.Positions, target.Positions, importedAt),
            Materialize(current?.Occupants, target.Occupants, importedAt),
            Materialize(current?.Authorities, target.Authorities, importedAt),
            Materialize(current?.Schedules, target.Schedules, importedAt),
            Materialize(current?.Relations, target.Relations, importedAt),
            Materialize(current?.ActionDomainCatalog, target.ActionDomainCatalog, importedAt));

        return new OrganizationImportResult(OrganizationImportStatus.Applied, plan, snapshot);
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
        AddSingleChange(
            changes,
            RegistryEntityKind.ActionDomainCatalog,
            target.OrganizationId.Value,
            current?.ActionDomainCatalog,
            target.ActionDomainCatalog);

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
        DateTimeOffset importedAt) =>
        current is not null
        && string.Equals(current.Fingerprint, target.Fingerprint, StringComparison.Ordinal)
            ? current
            : new RegistryEntry<T>(target.Value, target.Fingerprint, importedAt);

    private static IReadOnlyDictionary<TKey, RegistryEntry<TValue>> Materialize<TKey, TValue>(
        IReadOnlyDictionary<TKey, RegistryEntry<TValue>>? current,
        IReadOnlyDictionary<TKey, OrganizationRegistryProjection.ProjectedEntry<TValue>> target,
        DateTimeOffset importedAt)
        where TKey : notnull =>
        new ReadOnlyDictionary<TKey, RegistryEntry<TValue>>(
            target.ToDictionary(
                pair => pair.Key,
                pair => Materialize(
                    current is not null && current.TryGetValue(pair.Key, out var entry) ? entry : null,
                    pair.Value,
                    importedAt)));
}
