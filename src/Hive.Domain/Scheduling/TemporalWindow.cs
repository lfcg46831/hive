namespace Hive.Domain.Scheduling;

/// <summary>
/// The temporal window of a single schedule firing: the half-open interval
/// <c>[<see cref="Start"/>, <see cref="End"/>)</c> — start inclusive, end exclusive — matching the
/// working-hours semantics of §4.6. The window anchors the deterministic Pulse idempotency key
/// (see <see cref="PulseIdempotencyKey"/>), so two firings of the same schedule can only collide if
/// they share the exact same window.
/// </summary>
/// <remarks>
/// Both endpoints are normalized to UTC so the window (and any key derived from it) is independent of
/// the offset in which it was constructed; the original instants are preserved for display.
/// </remarks>
public sealed record TemporalWindow
{
    private TemporalWindow(DateTimeOffset start, DateTimeOffset end)
    {
        Start = start;
        End = end;
    }

    /// <summary>The inclusive start instant of the window.</summary>
    public DateTimeOffset Start { get; }

    /// <summary>The exclusive end instant of the window.</summary>
    public DateTimeOffset End { get; }

    /// <summary>The duration of the half-open interval.</summary>
    public TimeSpan Duration => End - Start;

    /// <summary>
    /// Builds a window from <paramref name="start"/> (inclusive) to <paramref name="end"/> (exclusive).
    /// The end must be strictly after the start; a zero-length or inverted window is rejected because it
    /// could not carry a well-defined firing.
    /// </summary>
    public static TemporalWindow From(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            throw new ArgumentException(
                "A temporal window end must be strictly after its start ([start, end) is half-open).",
                nameof(end));
        }

        return new TemporalWindow(start, end);
    }

    /// <summary>Whether <paramref name="instant"/> falls inside the half-open interval.</summary>
    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;

    /// <summary>The canonical UTC textual form of the window, used to compose deterministic keys.</summary>
    public string ToCanonicalString() =>
        $"{ToCanonicalInstant(Start)}/{ToCanonicalInstant(End)}";

    internal static string ToCanonicalInstant(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
