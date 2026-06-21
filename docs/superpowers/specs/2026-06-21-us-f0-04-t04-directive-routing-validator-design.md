# US-F0-04-T04 Directive Routing Validator Design

## Source of truth

The durable routing and validation contracts remain in `docs/bible.html`, especially sections 9.8 and US-F0-04. This document records only the implementation design for T04 and must not override the bible.

## Scope

Implement contextual validation for downward `Directive` messages. A valid route starts at a position and ends at one of that position's direct subordinates in the same organization. Report, escalation, approval, actor integration, public error normalization, and audit behavior remain outside T04.

## Component and API

Add a focused `DirectiveRoutingValidator` in `Hive.Domain.Messaging`. It receives `IOrganizationRelations` through its constructor and exposes:

```csharp
ValueTask<ValidationResult> ValidateAsync(
    Directive directive,
    CancellationToken cancellationToken = default);
```

`directive` and the injected dependency are required internal API inputs and fail fast when null. Cancellation is checked before registry access and is propagated.

The validator is intentionally specific to `Directive`. A generic vertical-routing orchestrator would anticipate T05 and T06 without an active requirement, while a new relation predicate on `IOrganizationRelations` would reopen the completed T01 contract.

## Validation flow

1. Require the directive endpoints to match the `PositionEndpointRef` to `PositionEndpointRef` path declared by `MessageRoutingRules` for `Directive`. A mismatch returns `endpoint-not-allowed` at the corresponding `from` or `to` path with `InvalidRoute`; it does not query the registry.
2. Probe the source with `GetUnitOfPositionAsync`. `OrganizationRelationNotFoundException` from this query means the organization is absent, so return `organization-not-found` at `organizationId` and stop because position checks are no longer evaluable.
3. Probe the destination with `GetUnitOfPositionAsync`. Null source and destination probes produce independent `position-not-found` errors at `from.positionId` and `to.positionId`, aggregated through `ValidationResult.Create`.
4. Only when both positions exist, call `GetDirectSuperiorAsync` for the destination. The route is valid when the returned superior equals the source position.
5. A different superior or a destination without a superior returns `direct-subordinate-required` at `to.positionId` with `InvalidRoute`.

The destination-superior lookup proves the same `DirectSuperiorToDirectSubordinate` relation declared by the routing matrix with one relation query and without loading a subordinate collection.

## Error and failure behavior

Confirmed registry absences are converted exactly as section 9.8 requires:

| Condition | Code | Path | Reason |
| --- | --- | --- | --- |
| Organization absent | `organization-not-found` | `organizationId` | `InvalidRoute` |
| Source position absent | `position-not-found` | `from.positionId` | `InvalidRoute` |
| Destination position absent | `position-not-found` | `to.positionId` | `InvalidRoute` |
| Destination is not a direct subordinate | `direct-subordinate-required` | `to.positionId` | `InvalidRoute` |

`OrganizationRelationNotFoundException` is caught only around organization-relation queries and mapped according to the query being executed. Cancellation, dependency unavailability, and unexpected exceptions propagate as technical failures; they are not converted to a definitive routing rejection.

## Tests

Unit tests use a small controllable `IOrganizationRelations` fake and cover:

- direct superior to direct subordinate is valid;
- a skipped level is rejected;
- an inverted route is rejected;
- a root-leadership destination is rejected;
- invalid endpoint variants are rejected without registry access;
- an unknown organization returns the canonical organization error and gates position checks;
- unknown source and destination positions are aggregated;
- a single unknown source or destination returns the correct canonical path;
- cancellation propagates without registry access;
- an unexpected registry exception propagates unchanged.

Focused validator tests run first. The complete `Hive.Tests` project and solution build provide regression verification after implementation.
