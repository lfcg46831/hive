# US-F0-03-T02 Common Message Envelope Design

## Goal

Define the immutable common envelope used by every organizational message, including typed endpoints and the canonical priority dependency required by `US-F0-03-T02`.

## Source of truth and scope

The implementation follows sections 9.1–9.4 and `US-F0-03-T02`/`US-F0-03-T03a` in `docs/bible.html`.

This task includes:

- the `Priority` contract assigned to prerequisite `US-F0-03-T03a`;
- the `EndpointRef` discriminated union and its initial variants;
- the abstract `OrgMessage` base record and its common metadata;
- focused domain tests for these contracts.

This task does not include concrete organizational message types, type-specific endpoint combinations, routing, Akka mailbox configuration, or wire serializer registration. Those responsibilities remain with `T03b`, `T04`–`T08`, and later tasks.

## Considered approaches

### Abstract base record

Define `OrgMessage` as an abstract record inherited by the concrete message records introduced by `T04`. This directly reflects the canonical sketch in the bible, centralizes the envelope contract, and prevents metadata drift. This is the selected approach.

### Composed envelope

Define a sealed `MessageEnvelope` value carried by each concrete message. Composition reduces inheritance, but it diverges from the canonical contract and adds an extra level at every consumer without a current need.

### Interface with repeated properties

Define an interface and repeat the properties on each message. This is flexible but duplicates a durable protocol contract and makes inconsistent implementations easier.

## Architecture and files

All new domain types live under `src/Hive.Domain/Messaging/` in the `Hive.Domain.Messaging` namespace. Identity types continue to come from `Hive.Domain.Identity`.

- `Priority.cs` defines the closed priority enum and the `PriorityContract` operations.
- `EndpointRef.cs` defines the abstract union and the three sealed endpoint variants.
- `SystemEndpointKind.cs` defines the two F0 system producer kinds.
- `OrgMessage.cs` defines the abstract common message record.

Tests live in `tests/Hive.Tests/` and are grouped by contract responsibility rather than concrete message type.

## Priority contract

`Priority` has exactly four values with stable semantic ranks:

- `Low = 1`
- `Normal = 2`
- `High = 3`
- `Critical = 4`

`PriorityContract.RequireDefined` validates an in-memory value, `Compare` compares semantic rank, `ToWireValue` produces the canonical lowercase name, `ParseWireValue` returns a priority or throws `ArgumentException`, and `TryParseWireValue` returns `false` without supplying a value. Both parsing operations accept only `low`, `normal`, `high`, or `critical`; they reject missing input, numeric strings, different capitalization, surrounding whitespace, and unknown values. Creation APIs may choose `Normal` before constructing a message, but neither the enum nor parsing silently supplies a default.

This contract does not configure an Akka priority mailbox. Serializer integration remains outside this task, although the canonical textual representation is exposed for that later integration.

## Endpoint references

`EndpointRef` is an abstract immutable record with three sealed variants:

- `PositionEndpointRef` carries a non-null `PositionId`.
- `OrganizationOwnerEndpointRef` carries no separate identifier; it resolves within the envelope's `OrganizationId`.
- `SystemEndpointRef` carries a defined `SystemEndpointKind`, restricted in F0 to `Scheduler` and `DomainEvents`.

Endpoints describe logical organizational producers and consumers. They do not expose Akka actor paths, node addresses, roles, or approval-policy selectors. Type-specific rules about which endpoint variants may be combined remain outside the envelope and will be implemented by `T05`/`T06`.

## Common message base

`OrgMessage` is an abstract record with these immutable properties:

- `MessageId Id`
- `OrganizationId OrganizationId`
- `EndpointRef From`
- `EndpointRef To`
- `ThreadId Thread`
- `Priority Priority`
- `int SchemaVersion`
- `DateTimeOffset SentAt`
- `DateTimeOffset? Deadline`

Its protected constructor rejects null required references, undefined priority values, and non-positive schema versions. It preserves the supplied timestamp and optional deadline without reading a clock. `SentAt` is the message-envelope timestamp; receipt, processing, and persistence timestamps belong to delivery and audit records.

The base does not validate endpoint routing combinations or compare `Deadline` with `SentAt`. Those are cross-field or message-type rules assigned to `T05`/`T06`.

## Data flow

A producer creates validated identity values, concrete endpoint references, and a priority, then constructs a concrete message record. The derived record passes the common metadata to `OrgMessage`. Consumers can inspect the complete envelope through the base contract while using pattern matching for endpoint variants and concrete message payloads.

No ambient services, clocks, serializers, actor runtime types, or infrastructure dependencies enter the domain contract.

## Error handling

Local construction errors fail immediately:

- null identity or endpoint references produce `ArgumentNullException`;
- undefined priority or system endpoint values produce `ArgumentOutOfRangeException`;
- a non-positive schema version produces `ArgumentOutOfRangeException`;
- invalid canonical priority text produces `ArgumentException` from `ParseWireValue` and `false` from `TryParseWireValue`.

Controlled protocol rejection at deserialization and message acceptance remains the responsibility of later validation and serialization tasks.

## Test strategy

Implementation follows test-driven development:

1. Write priority tests and verify that they fail before adding `Priority`.
2. Implement the minimal priority contract and make the focused tests pass.
3. Write endpoint tests and verify that they fail before adding endpoint types.
4. Implement the endpoint union and make the focused tests pass.
5. Write envelope tests using a private test-only derived record and verify that they fail before adding `OrgMessage`.
6. Implement the common base and make the focused tests pass.
7. Run all domain tests, the full solution test suite, a clean solution build, and repository formatting checks.

Tests cover exact enum ranks and ordering, canonical wire names and rejected inputs, endpoint equality and invalid values, propagation of every envelope property, record immutability, and local constructor failures. They deliberately exclude concrete message payloads, routing matrices, serializer round trips, and compatibility fixtures.

## Completion criteria

The task is complete when `Priority` satisfies the `T03a` contract without marking all of `T03` complete, endpoint references represent every F0 logical endpoint, `OrgMessage` exposes the complete common metadata from the bible, focused and full tests pass, and no actor or infrastructure dependency is introduced into `Hive.Domain`.
