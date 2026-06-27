using Hive.Actors.Sharding;
using Hive.Domain.Positions;

namespace Hive.Actors.Serialization;

/// <summary>
/// Stable manifest registry for the PositionActor sharded envelope, commands, persisted events and
/// snapshot (US-F0-06-T05b). These strings are the persisted/wire contract; CLR type names never
/// travel in remote messages or journal entries.
/// </summary>
internal static class PositionProtocolManifests
{
    private static readonly IReadOnlyList<(string Manifest, Type Type)> Entries =
    [
        ("position-envelope", typeof(PositionEnvelope)),
        ("accept-message", typeof(AcceptMessage)),
        ("open-task", typeof(OpenTask)),
        ("update-task", typeof(UpdateTask)),
        ("complete-task", typeof(CompleteTask)),
        ("update-short-memory", typeof(UpdateShortMemory)),
        ("change-occupant", typeof(ChangeOccupant)),
        ("request-passivation", typeof(RequestPassivation)),
        ("message-received", typeof(MessageReceived)),
        ("task-created", typeof(TaskCreated)),
        ("task-updated", typeof(TaskUpdated)),
        ("task-completed", typeof(TaskCompleted)),
        ("short-memory-updated", typeof(ShortMemoryUpdated)),
        ("occupant-changed", typeof(OccupantChanged)),
        ("message-dispatched", typeof(MessageDispatched)),
        ("position-passivated", typeof(PositionPassivated)),
        ("position-configuration-applied", typeof(PositionConfigurationApplied)),
        ("position-snapshot", typeof(PositionSnapshot)),
    ];

    private static readonly IReadOnlyDictionary<Type, string> ManifestByType =
        Entries.ToDictionary(entry => entry.Type, entry => entry.Manifest);

    private static readonly IReadOnlyDictionary<string, Type> TypeByManifest =
        Entries.ToDictionary(entry => entry.Manifest, entry => entry.Type, StringComparer.Ordinal);

    public static IReadOnlyCollection<Type> ProtocolTypes { get; } =
        Entries.Select(entry => entry.Type)
            .Concat(new[] { typeof(PositionCommand), typeof(PositionEvent) })
            .ToArray();

    public static string ForType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (ManifestByType.TryGetValue(type, out var manifest))
        {
            return manifest;
        }

        throw new ArgumentException(
            $"'{type.FullName}' is not a registered PositionActor protocol type.",
            nameof(type));
    }

    public static Type ForManifest(string manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (TypeByManifest.TryGetValue(manifest, out var type))
        {
            return type;
        }

        throw new ArgumentException(
            $"'{manifest}' is not a registered PositionActor protocol manifest.",
            nameof(manifest));
    }
}
