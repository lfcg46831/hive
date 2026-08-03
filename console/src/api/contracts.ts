/**
 * Wire contracts of the public HIVE API (`/api/v1`).
 *
 * These types mirror the published OpenAPI document one-to-one, including the
 * snake_case JSON property names. `openapiParity.contract.test.ts` verifies the
 * mirror mechanically against the exported document, so this file is never a
 * hand-maintained guess: a backend contract change fails the parity check.
 *
 * Nothing here may describe the private `/internal` surface.
 */

/** ISO-8601 timestamp in UTC, as serialized by the API. */
export type UtcTimestamp = string;

/** Occupant kinds declared by the organization registry. */
export type OrganizationOccupantType = 'AiAgent' | 'Human';

/**
 * Operational states in canonical precedence order, strongest first.
 * Precedence is resolved by the API; clients never re-derive it.
 */
export type PositionOperationalState =
  | 'Offline'
  | 'Blocked'
  | 'WaitingHuman'
  | 'Working'
  | 'Idle';

/** Monotonic version and fingerprint of the materialized registry snapshot. */
export interface RegistryVersion {
  version: number;
  fingerprint: string;
}

export interface OrganizationSummary {
  id: string;
  name: string | null;
  root_unit_id: string;
  root_position_id: string;
}

export interface OrganizationUnit {
  id: string;
  name: string | null;
  parent_unit_id: string | null;
  leadership_position_id: string;
}

export interface OrganizationOccupant {
  id: string | null;
  type: OrganizationOccupantType;
}

export interface PositionHierarchy {
  reports_to_position_id: string | null;
  direct_subordinate_position_ids: string[];
}

export interface PositionCorrelatedEvent {
  type: string;
  thread_id: string;
  occurred_at_utc: UtcTimestamp;
}

export interface OrganizationPositionState {
  position_id: string;
  state: PositionOperationalState;
  /** Monotonic per position; used to discard stale realtime notifications. */
  sequence: number;
  updated_at_utc: UtcTimestamp;
  last_correlated_event: PositionCorrelatedEvent | null;
}

export interface OrganizationPosition {
  id: string;
  name: string | null;
  unit_id: string;
  occupant: OrganizationOccupant;
  hierarchy: PositionHierarchy;
  operational_state: OrganizationPositionState;
}

export interface OrganogramResponse {
  registry: RegistryVersion;
  generated_at_utc: UtcTimestamp;
  root_unit_id: string;
  organization: OrganizationSummary;
  units: OrganizationUnit[];
  positions: OrganizationPosition[];
}

export interface PositionDetailResponse {
  registry: RegistryVersion;
  generated_at_utc: UtcTimestamp;
  position: OrganizationPosition;
}

export interface PositionStatesResponse {
  registry: RegistryVersion;
  generated_at_utc: UtcTimestamp;
  /** Staleness signal: timestamp of the last event applied by the projection. */
  last_event_applied_at_utc: UtcTimestamp | null;
  states: OrganizationPositionState[];
}

/** RFC 7807 payload returned by every failing public endpoint. */
export interface ProblemDetails {
  type?: string | null;
  title?: string | null;
  status?: number | null;
  detail?: string | null;
  instance?: string | null;
  [extension: string]: unknown;
}

/** SignalR notification: the published registry snapshot changed. */
export interface OrganogramChangedNotification {
  organization_id: string;
  registry: RegistryVersion;
  changed_at_utc: UtcTimestamp;
}

/** SignalR notification: one position moved to a new operational state. */
export interface PositionStateChangedNotification {
  organization_id: string;
  state: OrganizationPositionState;
}
