namespace Hive.Domain.Positions;

/// <summary>
/// The closed set of internal commands accepted by a <c>PositionActor</c> (US-F0-06-T02): the
/// intents that drive its persisted state — accepting an inbound message
/// (<see cref="AcceptMessage"/>), opening/updating/completing a task (<see cref="OpenTask"/>,
/// <see cref="UpdateTask"/>, <see cref="CompleteTask"/>), updating short-term memory
/// (<see cref="UpdateShortMemory"/>), changing the occupant (<see cref="ChangeOccupant"/>) and
/// requesting passivation (<see cref="RequestPassivation"/>).
/// </summary>
/// <remarks>
/// <para>
/// Commands are <em>intents</em>, distinct from the persisted events and snapshots of
/// US-F0-06-T03: a command may be rejected (invalid, duplicate, unauthorized) and only a successful
/// command yields events. Validating a command against the entity state, persisting the resulting
/// events and enforcing idempotency (US-F0-06-T07) and passivation guard rails (US-F0-06-T11) belong
/// to later tasks; this contract only fixes the shape of the inputs.
/// </para>
/// <para>
/// They are pure domain contracts and carry no addressing: Cluster Sharding (US-F0-06-T04) delivers
/// each command to the entity identified by <c>OrganizationId/PositionId</c>, so the entity already
/// knows its own identity and the command does not repeat it. The only exception is
/// <see cref="AcceptMessage"/>, whose envelope independently carries its own <c>OrganizationId</c>.
/// </para>
/// </remarks>
public abstract record PositionCommand;
