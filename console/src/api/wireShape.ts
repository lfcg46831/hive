/**
 * Runtime description of the wire shape declared in `contracts.ts`.
 *
 * TypeScript types are erased at runtime, so the parity check against the
 * published OpenAPI document needs the property names as data. The
 * `AllKeysCovered` assertions below make `tsc` fail whenever a contract gains a
 * property that is not listed here, so the two views cannot drift apart.
 */

import type {
  OrganizationOccupant,
  OrganizationOccupantType,
  OrganizationPosition,
  OrganizationPositionState,
  OrganizationSummary,
  OrganizationUnit,
  OrganogramResponse,
  PositionCorrelatedEvent,
  PositionDetailResponse,
  PositionHierarchy,
  PositionOperationalState,
  PositionStatesResponse,
  RegistryVersion,
} from './contracts.js';

/** `true` only when `Keys` covers every property of `T`. */
type AllKeysCovered<T, Keys extends readonly (keyof T)[]> =
  Exclude<keyof T, Keys[number]> extends never
    ? true
    : { missingProperties: Exclude<keyof T, Keys[number]> };

const registryVersion = ['version', 'fingerprint'] as const;
const organizationSummary = ['id', 'name', 'root_unit_id', 'root_position_id'] as const;
const organizationUnit = [
  'id',
  'name',
  'parent_unit_id',
  'leadership_position_id',
] as const;
const organizationOccupant = ['id', 'type'] as const;
const positionHierarchy = [
  'reports_to_position_id',
  'direct_subordinate_position_ids',
] as const;
const positionCorrelatedEvent = ['type', 'thread_id', 'occurred_at_utc'] as const;
const organizationPositionState = [
  'position_id',
  'state',
  'sequence',
  'updated_at_utc',
  'last_correlated_event',
] as const;
const organizationPosition = [
  'id',
  'name',
  'unit_id',
  'occupant',
  'hierarchy',
  'operational_state',
] as const;
const organogramResponse = [
  'registry',
  'generated_at_utc',
  'root_unit_id',
  'organization',
  'units',
  'positions',
] as const;
const positionDetailResponse = ['registry', 'generated_at_utc', 'position'] as const;
const positionStatesResponse = [
  'registry',
  'generated_at_utc',
  'last_event_applied_at_utc',
  'states',
] as const;

const _coverage: [
  AllKeysCovered<RegistryVersion, typeof registryVersion>,
  AllKeysCovered<OrganizationSummary, typeof organizationSummary>,
  AllKeysCovered<OrganizationUnit, typeof organizationUnit>,
  AllKeysCovered<OrganizationOccupant, typeof organizationOccupant>,
  AllKeysCovered<PositionHierarchy, typeof positionHierarchy>,
  AllKeysCovered<PositionCorrelatedEvent, typeof positionCorrelatedEvent>,
  AllKeysCovered<OrganizationPositionState, typeof organizationPositionState>,
  AllKeysCovered<OrganizationPosition, typeof organizationPosition>,
  AllKeysCovered<OrganogramResponse, typeof organogramResponse>,
  AllKeysCovered<PositionDetailResponse, typeof positionDetailResponse>,
  AllKeysCovered<PositionStatesResponse, typeof positionStatesResponse>,
] = [true, true, true, true, true, true, true, true, true, true, true];
void _coverage;

/** Object schemas keyed by their OpenAPI component name. */
export const objectWireShape = {
  RegistryVersion: registryVersion,
  OrganizationSummary: organizationSummary,
  OrganizationUnit: organizationUnit,
  OrganizationOccupant: organizationOccupant,
  PositionHierarchy: positionHierarchy,
  PositionCorrelatedEvent: positionCorrelatedEvent,
  OrganizationPositionState: organizationPositionState,
  OrganizationPosition: organizationPosition,
  OrganogramResponse: organogramResponse,
  PositionDetailResponse: positionDetailResponse,
  PositionStatesResponse: positionStatesResponse,
} as const satisfies Record<string, readonly string[]>;

const occupantTypes: readonly OrganizationOccupantType[] = ['AiAgent', 'Human'];
const operationalStates: readonly PositionOperationalState[] = [
  'Offline',
  'Blocked',
  'WaitingHuman',
  'Working',
  'Idle',
];

/** Enum schemas keyed by their OpenAPI component name, in declaration order. */
export const enumWireShape = {
  OrganizationOccupantType: occupantTypes,
  PositionOperationalState: operationalStates,
} as const satisfies Record<string, readonly string[]>;
