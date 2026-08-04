/**
 * Runtime description of the wire shape declared in `contracts.ts`.
 *
 * TypeScript types are erased at runtime, so the parity check against the
 * published OpenAPI document needs their schema semantics as data. The generic
 * builders below make `tsc` reject missing properties, wrong nullability and
 * wrong referenced DTOs before the runtime parity check compares the descriptor
 * with OpenAPI.
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
  ProblemDetails,
  RegistryVersion,
} from './contracts.js';

type ReferenceSchemaName<T> =
  NonNullable<T> extends RegistryVersion
    ? 'RegistryVersion'
    : NonNullable<T> extends OrganizationSummary
      ? 'OrganizationSummary'
      : NonNullable<T> extends OrganizationUnit
        ? 'OrganizationUnit'
        : NonNullable<T> extends OrganizationOccupant
          ? 'OrganizationOccupant'
          : NonNullable<T> extends PositionHierarchy
            ? 'PositionHierarchy'
            : NonNullable<T> extends PositionCorrelatedEvent
              ? 'PositionCorrelatedEvent'
              : NonNullable<T> extends OrganizationPositionState
                ? 'OrganizationPositionState'
                : NonNullable<T> extends OrganizationPosition
                  ? 'OrganizationPosition'
                  : NonNullable<T> extends OrganogramResponse
                    ? 'OrganogramResponse'
                    : NonNullable<T> extends PositionDetailResponse
                      ? 'PositionDetailResponse'
                      : NonNullable<T> extends PositionStatesResponse
                        ? 'PositionStatesResponse'
                        : NonNullable<T> extends OrganizationOccupantType
                          ? 'OrganizationOccupantType'
                          : NonNullable<T> extends PositionOperationalState
                            ? 'PositionOperationalState'
                            : never;

type Requiredness<T> = undefined extends T
  ? { required: false }
  : { required: true };

type Nullability<T> = null extends T
  ? { nullable: true }
  : { nullable: false };

type ArrayItemWireShape<T> = [ReferenceSchemaName<T>] extends [never]
  ? NonNullable<T> extends string
    ? { type: 'string' }
    : never
  : { type: 'reference'; schema: ReferenceSchemaName<T> };

type PropertyKindWireShape<T> = NonNullable<T> extends readonly (infer TItem)[]
  ? { type: 'array'; items: ArrayItemWireShape<TItem> }
  : [ReferenceSchemaName<T>] extends [never]
    ? NonNullable<T> extends number
      ? { type: 'integer'; format: 'int32' | 'int64' }
      : NonNullable<T> extends string
        ? { type: 'string'; format?: 'date-time' | 'uuid' }
        : never
    : { type: 'reference'; schema: ReferenceSchemaName<T> };

export type RuntimePropertyWireShape =
  | {
      type: 'string';
      format?: 'date-time' | 'uuid';
      required: boolean;
      nullable: boolean;
    }
  | {
      type: 'integer';
      format: 'int32' | 'int64';
      required: boolean;
      nullable: boolean;
    }
  | {
      type: 'reference';
      schema: string;
      required: boolean;
      nullable: boolean;
    }
  | {
      type: 'array';
      items: { type: 'string' } | { type: 'reference'; schema: string };
      required: boolean;
      nullable: boolean;
    };

export type PropertyWireShape<T> = PropertyKindWireShape<T> &
  Requiredness<T> &
  Nullability<T>;

type ObjectPropertiesWireShape<T> = {
  [Property in keyof T]-?: PropertyWireShape<T[Property]>;
};

const defineObjectWireShape =
  <T>(additionalProperties: boolean) =>
  (properties: ObjectPropertiesWireShape<T>) => ({
    additionalProperties,
    properties,
  });

/** Object schemas keyed by their OpenAPI component name. */
export const objectWireShape = {
  RegistryVersion: defineObjectWireShape<RegistryVersion>(false)({
    version: { type: 'integer', format: 'int64', required: true, nullable: false },
    fingerprint: { type: 'string', required: true, nullable: false },
  }),
  OrganizationSummary: defineObjectWireShape<OrganizationSummary>(false)({
    id: { type: 'string', required: true, nullable: false },
    name: { type: 'string', required: true, nullable: true },
    root_unit_id: { type: 'string', required: true, nullable: false },
    root_position_id: { type: 'string', required: true, nullable: false },
  }),
  OrganizationUnit: defineObjectWireShape<OrganizationUnit>(false)({
    id: { type: 'string', required: true, nullable: false },
    name: { type: 'string', required: true, nullable: true },
    parent_unit_id: { type: 'string', required: true, nullable: true },
    leadership_position_id: { type: 'string', required: true, nullable: false },
  }),
  OrganizationOccupant: defineObjectWireShape<OrganizationOccupant>(false)({
    id: { type: 'string', required: true, nullable: true },
    type: {
      type: 'reference',
      schema: 'OrganizationOccupantType',
      required: true,
      nullable: false,
    },
  }),
  PositionHierarchy: defineObjectWireShape<PositionHierarchy>(false)({
    reports_to_position_id: { type: 'string', required: true, nullable: true },
    direct_subordinate_position_ids: {
      type: 'array',
      items: { type: 'string' },
      required: true,
      nullable: false,
    },
  }),
  PositionCorrelatedEvent: defineObjectWireShape<PositionCorrelatedEvent>(false)({
    type: { type: 'string', required: true, nullable: false },
    thread_id: {
      type: 'string',
      format: 'uuid',
      required: true,
      nullable: false,
    },
    occurred_at_utc: {
      type: 'string',
      format: 'date-time',
      required: true,
      nullable: false,
    },
  }),
  OrganizationPositionState: defineObjectWireShape<OrganizationPositionState>(false)({
    position_id: { type: 'string', required: true, nullable: false },
    state: {
      type: 'reference',
      schema: 'PositionOperationalState',
      required: true,
      nullable: false,
    },
    sequence: { type: 'integer', format: 'int64', required: true, nullable: false },
    updated_at_utc: {
      type: 'string',
      format: 'date-time',
      required: true,
      nullable: false,
    },
    last_correlated_event: {
      type: 'reference',
      schema: 'PositionCorrelatedEvent',
      required: true,
      nullable: true,
    },
  }),
  OrganizationPosition: defineObjectWireShape<OrganizationPosition>(false)({
    id: { type: 'string', required: true, nullable: false },
    name: { type: 'string', required: true, nullable: true },
    unit_id: { type: 'string', required: true, nullable: false },
    occupant: {
      type: 'reference',
      schema: 'OrganizationOccupant',
      required: true,
      nullable: false,
    },
    hierarchy: {
      type: 'reference',
      schema: 'PositionHierarchy',
      required: true,
      nullable: false,
    },
    operational_state: {
      type: 'reference',
      schema: 'OrganizationPositionState',
      required: true,
      nullable: false,
    },
  }),
  OrganogramResponse: defineObjectWireShape<OrganogramResponse>(false)({
    registry: {
      type: 'reference',
      schema: 'RegistryVersion',
      required: true,
      nullable: false,
    },
    generated_at_utc: {
      type: 'string',
      format: 'date-time',
      required: true,
      nullable: false,
    },
    root_unit_id: { type: 'string', required: true, nullable: false },
    organization: {
      type: 'reference',
      schema: 'OrganizationSummary',
      required: true,
      nullable: false,
    },
    units: {
      type: 'array',
      items: { type: 'reference', schema: 'OrganizationUnit' },
      required: true,
      nullable: false,
    },
    positions: {
      type: 'array',
      items: { type: 'reference', schema: 'OrganizationPosition' },
      required: true,
      nullable: false,
    },
  }),
  PositionDetailResponse: defineObjectWireShape<PositionDetailResponse>(false)({
    registry: {
      type: 'reference',
      schema: 'RegistryVersion',
      required: true,
      nullable: false,
    },
    generated_at_utc: {
      type: 'string',
      format: 'date-time',
      required: true,
      nullable: false,
    },
    position: {
      type: 'reference',
      schema: 'OrganizationPosition',
      required: true,
      nullable: false,
    },
  }),
  PositionStatesResponse: defineObjectWireShape<PositionStatesResponse>(false)({
    registry: {
      type: 'reference',
      schema: 'RegistryVersion',
      required: true,
      nullable: false,
    },
    generated_at_utc: {
      type: 'string',
      format: 'date-time',
      required: true,
      nullable: false,
    },
    last_event_applied_at_utc: {
      type: 'string',
      format: 'date-time',
      required: true,
      nullable: true,
    },
    states: {
      type: 'array',
      items: { type: 'reference', schema: 'OrganizationPositionState' },
      required: true,
      nullable: false,
    },
  }),
  ProblemDetails: defineObjectWireShape<
    Pick<ProblemDetails, 'type' | 'title' | 'status' | 'detail' | 'instance'>
  >(true)({
    type: { type: 'string', required: false, nullable: true },
    title: { type: 'string', required: false, nullable: true },
    status: { type: 'integer', format: 'int32', required: false, nullable: true },
    detail: { type: 'string', required: false, nullable: true },
    instance: { type: 'string', required: false, nullable: true },
  }),
};

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
