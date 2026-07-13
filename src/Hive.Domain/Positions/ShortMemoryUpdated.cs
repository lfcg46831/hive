namespace Hive.Domain.Positions;

/// <summary>
/// An entry of the position's short-term memory was upserted — the fact produced by a successful
/// <see cref="UpdateShortMemory"/>. The <see cref="Key"/> identifies the entry and carries content;
/// the <see cref="Value"/> is the new content and may be empty (an empty value is the meaningful
/// "cleared" entry the reducer interprets, not an error).
/// </summary>
public sealed record ShortMemoryUpdated : PositionEvent
{
    public ShortMemoryUpdated(
        string key,
        string value,
        DateTimeOffset occurredAt,
        ShortMemoryContextScope? contextScope = null)
        : base(occurredAt)
    {
        Key = CommandText.RequireContent(key, nameof(key));
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        ContextScope = contextScope;
    }

    /// <summary>The short-memory entry key.</summary>
    public string Key { get; }

    /// <summary>The new content for the entry; may be empty.</summary>
    public string Value { get; }

    /// <summary>The optional durable scope used by bounded AI-context selection.</summary>
    public ShortMemoryContextScope? ContextScope { get; }
}
