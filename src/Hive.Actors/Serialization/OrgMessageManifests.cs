using Hive.Domain.Messaging;

namespace Hive.Actors.Serialization;

/// <summary>
/// Bidirectional registry between the canonical message-type manifest (a stable lowercase/kebab-case
/// token) and the concrete <see cref="OrgMessage"/> CLR type. The manifest — not a CLR type name —
/// is what travels on the wire and in persisted journals, so renaming or moving a record never
/// changes the stored manifest. Adding a new message type is an explicit, additive entry here.
/// </summary>
internal static class OrgMessageManifests
{
    private static readonly IReadOnlyList<(string Manifest, Type Type)> Entries =
    [
        ("directive", typeof(Directive)),
        ("report", typeof(Report)),
        ("memo", typeof(Memo)),
        ("escalation", typeof(Escalation)),
        ("peer-request", typeof(PeerRequest)),
        ("peer-response", typeof(PeerResponse)),
        ("approval-request", typeof(ApprovalRequest)),
        ("approval-decision", typeof(ApprovalDecision)),
        ("pulse", typeof(Pulse)),
        ("event-trigger", typeof(EventTrigger)),
    ];

    private static readonly IReadOnlyDictionary<Type, string> ManifestByType =
        Entries.ToDictionary(entry => entry.Type, entry => entry.Manifest);

    private static readonly IReadOnlyDictionary<string, Type> TypeByManifest =
        Entries.ToDictionary(entry => entry.Manifest, entry => entry.Type, StringComparer.Ordinal);

    /// <summary>All canonical message types the serializer is responsible for.</summary>
    public static IReadOnlyCollection<Type> MessageTypes { get; } =
        Entries.Select(entry => entry.Type).ToArray();

    public static string ForType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (ManifestByType.TryGetValue(type, out var manifest))
        {
            return manifest;
        }

        throw new ArgumentException(
            $"'{type.FullName}' is not a registered organizational message type.",
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
            $"'{manifest}' is not a registered organizational message manifest.",
            nameof(manifest));
    }
}
