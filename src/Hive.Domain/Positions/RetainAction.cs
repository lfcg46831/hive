namespace Hive.Domain.Positions;

public sealed record RetainAction : PositionCommand
{
    public RetainAction(PersistedRetainedAction action)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public PersistedRetainedAction Action { get; }
}

public sealed record ActionRetained : PositionEvent
{
    public ActionRetained(PersistedRetainedAction action)
        : base((action ?? throw new ArgumentNullException(nameof(action))).RetainedAt)
    {
        Action = action;
    }

    public PersistedRetainedAction Action { get; }
}
