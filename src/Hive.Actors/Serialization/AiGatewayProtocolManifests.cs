using Hive.Actors.Gateway;

namespace Hive.Actors.Serialization;

/// <summary>
/// Stable manifest registry for the sharded AI gateway protocol (US-F1-05-T07). These strings are
/// the wire contract; CLR type names never travel in remote messages.
/// </summary>
internal static class AiGatewayProtocolManifests
{
    private static readonly IReadOnlyList<(string Manifest, Type Type)> Entries =
    [
        ("ai-gateway-envelope", typeof(AiGatewayEnvelope)),
        ("complete-ai-gateway-call", typeof(CompleteAiGatewayCall)),
        ("cancel-ai-gateway-call", typeof(CancelAiGatewayCall)),
        ("ai-gateway-call-completed", typeof(AiGatewayCallCompleted)),
        ("ai-gateway-call-canceled", typeof(AiGatewayCallCanceled)),
    ];

    private static readonly IReadOnlyDictionary<Type, string> ManifestByType =
        Entries.ToDictionary(entry => entry.Type, entry => entry.Manifest);

    private static readonly IReadOnlyDictionary<string, Type> TypeByManifest =
        Entries.ToDictionary(entry => entry.Manifest, entry => entry.Type, StringComparer.Ordinal);

    /// <summary>
    /// Types bound to the gateway serializer. The abstract command base is included so a nested
    /// polymorphic payload resolves through the same registry.
    /// </summary>
    public static IReadOnlyCollection<Type> ProtocolTypes { get; } =
        Entries.Select(entry => entry.Type)
            .Concat(new[] { typeof(AiGatewayProviderCommand) })
            .ToArray();

    public static string ForType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (ManifestByType.TryGetValue(type, out var manifest))
        {
            return manifest;
        }

        throw new ArgumentException(
            $"'{type.FullName}' is not a registered AI gateway protocol type.",
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
            $"'{manifest}' is not a registered AI gateway protocol manifest.",
            nameof(manifest));
    }
}
