/**
 * Snapshot fixtures shared by the frontend tests (US-F1-01-T14).
 *
 * The organization built here is deliberately not the flat F0 example: it has
 * three nested units, a human and an AI occupant, a vacant position and one
 * position in every state the filters can ask about, so a rendering test can
 * assert hierarchy, occupancy and state without inventing a second notion of
 * what the API returns. Every value is shaped exactly like the public wire
 * contract in `api/contracts.ts` — a fixture that drifts from the contract would
 * make these tests agree with a console the API never talks to.
 *
 * Not part of the shipped bundle: nothing outside `*.test.tsx` imports it.
 */

import type {
  OrganizationOccupant,
  OrganizationPosition,
  OrganizationPositionState,
  OrganizationUnit,
  OrganogramResponse,
  PositionCorrelatedEvent,
  PositionOperationalState,
  PositionStatesResponse,
} from '../../api/index.js';

export const ORGANIZATION_ID = 'acme-delivery';
export const GENERATED_AT_UTC = '2026-08-03T10:00:00.000Z';

export interface PositionOptions {
  readonly id: string;
  readonly name?: string | null;
  readonly unitId: string;
  readonly occupant?: OrganizationOccupant;
  readonly reportsTo?: string | null;
  readonly subordinates?: readonly string[];
  readonly state?: PositionOperationalState;
  readonly sequence?: number;
  readonly updatedAtUtc?: string;
  readonly event?: PositionCorrelatedEvent | null;
}

export function unit(
  id: string,
  parentUnitId: string | null,
  leadershipPositionId: string,
  name: string | null = id,
): OrganizationUnit {
  return { id, name, parent_unit_id: parentUnitId, leadership_position_id: leadershipPositionId };
}

export function position(options: PositionOptions): OrganizationPosition {
  return {
    id: options.id,
    name: options.name === undefined ? options.id : options.name,
    unit_id: options.unitId,
    occupant: options.occupant ?? { id: `${options.id}-agent`, type: 'AiAgent' },
    hierarchy: {
      reports_to_position_id: options.reportsTo ?? null,
      direct_subordinate_position_ids: [...(options.subordinates ?? [])],
    },
    operational_state: positionState({
      positionId: options.id,
      state: options.state ?? 'Idle',
      sequence: options.sequence ?? 1,
      updatedAtUtc: options.updatedAtUtc ?? '2026-08-03T09:59:00.000Z',
      event: options.event ?? null,
    }),
  };
}

export function positionState(options: {
  readonly positionId: string;
  readonly state: PositionOperationalState;
  readonly sequence: number;
  readonly updatedAtUtc?: string;
  readonly event?: PositionCorrelatedEvent | null;
}): OrganizationPositionState {
  return {
    position_id: options.positionId,
    state: options.state,
    sequence: options.sequence,
    updated_at_utc: options.updatedAtUtc ?? '2026-08-03T09:59:00.000Z',
    last_correlated_event: options.event ?? null,
  };
}

export function snapshot(overrides: Partial<OrganogramResponse> = {}): OrganogramResponse {
  return {
    registry: { version: 7, fingerprint: 'a1b2c3' },
    generated_at_utc: GENERATED_AT_UTC,
    root_unit_id: 'delivery',
    organization: {
      id: ORGANIZATION_ID,
      name: 'Acme Delivery',
      root_unit_id: 'delivery',
      root_position_id: 'head-of-delivery',
    },
    units: [],
    positions: [],
    ...overrides,
  };
}

/**
 * Delivery → Platform → Runtime, with leadership at every level, a human at the
 * top, an AI agent in the middle and a vacant position at the bottom.
 */
export function deliveryOrganization(): OrganogramResponse {
  return snapshot({
    units: [
      unit('delivery', null, 'head-of-delivery', 'Delivery'),
      unit('platform', 'delivery', 'platform-lead', 'Platform'),
      unit('runtime', 'platform', 'runtime-lead', 'Runtime squad'),
    ],
    positions: [
      position({
        id: 'head-of-delivery',
        name: 'Head of Delivery',
        unitId: 'delivery',
        occupant: { id: 'ana.sousa', type: 'Human' },
        subordinates: ['platform-lead'],
        state: 'WaitingHuman',
        sequence: 4,
        event: {
          type: 'ApprovalRequest',
          thread_id: 'thread-9',
          occurred_at_utc: '2026-08-03T09:58:00.000Z',
        },
      }),
      position({
        id: 'platform-lead',
        name: 'Platform Lead',
        unitId: 'platform',
        occupant: { id: 'agent-platform', type: 'AiAgent' },
        reportsTo: 'head-of-delivery',
        subordinates: ['runtime-lead'],
        state: 'Working',
        sequence: 11,
      }),
      position({
        id: 'runtime-lead',
        name: 'Runtime Lead',
        unitId: 'runtime',
        occupant: { id: 'agent-runtime', type: 'AiAgent' },
        reportsTo: 'platform-lead',
        state: 'Blocked',
        sequence: 3,
      }),
      position({
        id: 'runtime-engineer',
        name: 'Runtime Engineer',
        unitId: 'runtime',
        occupant: { id: null, type: 'AiAgent' },
        reportsTo: 'runtime-lead',
        state: 'Idle',
        sequence: 1,
      }),
    ],
  });
}

export function positionStatesResponse(
  states: readonly OrganizationPositionState[],
  overrides: Partial<PositionStatesResponse> = {},
): PositionStatesResponse {
  return {
    registry: { version: 7, fingerprint: 'a1b2c3' },
    generated_at_utc: '2026-08-03T10:00:30.000Z',
    last_event_applied_at_utc: '2026-08-03T10:00:29.000Z',
    states: [...states],
    ...overrides,
  };
}
