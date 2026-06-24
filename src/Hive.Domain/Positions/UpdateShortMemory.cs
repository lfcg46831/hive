namespace Hive.Domain.Positions;

/// <summary>
/// Upsert an entry in the position's short-term memory — the recent operational context
/// (current conversation/task, working notes) that survives restart as part of the entity state.
/// The <see cref="Key"/> identifies the entry and must carry content; the <see cref="Value"/> is the
/// new content and may be empty (an empty value is a meaningful "cleared" entry that the reducer
/// interprets, not an error).
/// </summary>
public sealed record UpdateShortMemory : PositionCommand
{
    public UpdateShortMemory(string key, string value)
    {
        Key = CommandText.RequireContent(key, nameof(key));
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The short-memory entry key.</summary>
    public string Key { get; }

    /// <summary>The new content for the entry; may be empty.</summary>
    public string Value { get; }
}
