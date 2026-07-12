using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class RetainedActionLifecycleTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Authorized_action_can_be_consumed_once()
    {
        var action = Action();
        var grant = Grant(action);
        var authorized = Retained(action).Apply(new RetainedActionAuthorized(grant, At.AddMinutes(1)));

        Assert.Equal(RetainedActionState.Authorized, Current(authorized).State);
        Assert.Equal(grant, Current(authorized).ActiveGrant);

        var consumed = authorized
            .Apply(new RetainedActionConsumed(action.Id, grant.Id, At.AddMinutes(2)))
            .Apply(new RetainedActionConsumed(action.Id, grant.Id, At.AddMinutes(3)));

        Assert.Equal(RetainedActionState.Consumed, Current(consumed).State);
        Assert.Null(Current(consumed).ActiveGrant);
        Assert.Equal(grant, Current(consumed).AuthorizationGrant);
        Assert.Equal(At.AddMinutes(2), Current(consumed).StateChangedAt);
    }

    [Fact]
    public void Retained_action_can_be_denied_and_terminal()
    {
        var action = Action();
        var denial = Denial(action);
        var denied = Retained(action)
            .Apply(new RetainedActionDenied(denial, At.AddMinutes(1)))
            .Apply(new RetainedActionAuthorized(Grant(action), At.AddMinutes(2)));

        Assert.Equal(RetainedActionState.Denied, Current(denied).State);
        Assert.Equal(denial, Current(denied).AuthorizationDenial);
        Assert.Null(Current(denied).ActiveGrant);
    }

    [Fact]
    public void Expiry_is_terminal_and_preserves_re_escalation_context()
    {
        var action = Action();
        var grant = Grant(action);
        var expired = Retained(action)
            .Apply(new RetainedActionAuthorized(grant, At.AddMinutes(1)))
            .Apply(new RetainedActionExpired(
                action.Id,
                grant.Id,
                "authorization-expired",
                At.AddMinutes(2)));

        Assert.Equal(RetainedActionState.Expired, Current(expired).State);
        Assert.Equal("authorization-expired", Current(expired).ReEscalationCode);
        Assert.Equal(grant, Current(expired).AuthorizationGrant);
        Assert.Null(Current(expired).ActiveGrant);
    }

    [Fact]
    public void Policy_tightening_returns_to_retained_and_allows_one_replacement_grant()
    {
        var action = Action();
        var first = Grant(action);
        var second = Grant(action, MessageId.New());
        var returned = Retained(action)
            .Apply(new RetainedActionAuthorized(first, At.AddMinutes(1)))
            .Apply(new RetainedActionReturned(
                action.Id,
                first.Id,
                "policy-tightened",
                At.AddMinutes(2)));

        Assert.Equal(RetainedActionState.Retained, Current(returned).State);
        Assert.Null(Current(returned).ActiveGrant);
        Assert.Equal(first, Current(returned).AuthorizationGrant);

        var repeated = returned.Apply(new RetainedActionAuthorized(first, At.AddMinutes(3)));
        Assert.Same(returned, repeated);

        var reauthorized = repeated.Apply(new RetainedActionAuthorized(second, At.AddMinutes(4)));

        Assert.Equal(second, Current(reauthorized).ActiveGrant);
        Assert.NotEqual(first.Id, Current(reauthorized).ActiveGrant!.Id);
    }

    [Fact]
    public void Missing_action_wrong_route_and_wrong_grant_are_no_ops()
    {
        var action = Action();
        var grant = Grant(action);
        var initial = Retained(action);
        var wrongRoute = Grant(action, to: PositionId.From("another-position"));

        Assert.Same(initial, initial.Apply(new RetainedActionAuthorized(wrongRoute, At.AddMinutes(1))));
        Assert.Same(initial, initial.Apply(new RetainedActionConsumed(action.Id, grant.Id, At.AddMinutes(1))));

        var authorized = initial.Apply(new RetainedActionAuthorized(grant, At.AddMinutes(1)));
        var unchanged = authorized.Apply(new RetainedActionConsumed(
            action.Id,
            MessageId.New(),
            At.AddMinutes(2)));

        Assert.Same(authorized, unchanged);
    }

    [Fact]
    public void Snapshot_preserves_authorized_lifecycle_and_active_grant()
    {
        var action = Action();
        var grant = Grant(action);
        var state = Retained(action).Apply(new RetainedActionAuthorized(grant, At.AddMinutes(1)));

        var restored = PositionState.Restore(state.ToSnapshot(At.AddMinutes(2)));

        Assert.Equal(Current(state), Current(restored));
        Assert.Equal(grant, Current(restored).ActiveGrant);
    }

    private static PositionState Retained(PersistedRetainedAction action) =>
        PositionState.Empty.Apply(new ActionRetained(action));

    private static PersistedRetainedAction Current(PositionState state) =>
        Assert.Single(state.RetainedActions).Value;

    private static PersistedRetainedAction Action() =>
        new(
            RetainedActionId.New(),
            ActionFingerprint.From("sha256:0000000000000000000000000000000000000000000000000000000000000012"),
            RetainedActionKind.Tool,
            "github.create-issue",
            "{\"arguments\":{\"title\":\"Incident\"}}",
            "{\"repository\":\"acme/hive\"}",
            "directive:lifecycle",
            OrganizationId.From("acme"),
            PositionId.From("delivery-lead"),
            ThreadId.New(),
            MessageId.New(),
            DirectiveId.New(),
            null,
            "action-gate-escalation-required",
            At);

    private static AuthorizationGrant Grant(
        PersistedRetainedAction action,
        MessageId? id = null,
        PositionId? to = null) =>
        new(
            id ?? MessageId.New(),
            action.OrganizationId,
            new OrganizationOwnerEndpointRef(),
            new PositionEndpointRef(to ?? action.PositionId),
            action.ThreadId,
            Priority.High,
            1,
            At,
            null,
            MessageId.New(),
            action.Id,
            action.Fingerprint,
            AuthorityKey.From("governance.authorize-retained-action"),
            At.AddHours(1),
            null);

    private static AuthorizationDenial Denial(PersistedRetainedAction action) =>
        new(
            MessageId.New(),
            action.OrganizationId,
            new OrganizationOwnerEndpointRef(),
            new PositionEndpointRef(action.PositionId),
            action.ThreadId,
            Priority.High,
            1,
            At,
            null,
            MessageId.New(),
            action.Id,
            "Denied by organization owner.");
}
