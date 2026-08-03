import { describe, expect, it } from 'vitest';
import type {
  OrganizationPosition,
  OrganizationPositionState,
  OrganizationUnit,
  OrganogramResponse,
} from '../../api/index.js';
import {
  buildOrganogramTree,
  countPositions,
  nodePositions,
  resolvePositionState,
} from './organogramTree.js';

describe('buildOrganogramTree', () => {
  it('nests units under their parent and keeps snapshot order', () => {
    const tree = buildOrganogramTree(
      snapshot({
        rootUnitId: 'root',
        units: [
          unit('root', null, 'root-lead'),
          unit('delivery', 'root', 'delivery-lead'),
          unit('support', 'root', 'support-lead'),
          unit('squad', 'delivery', 'squad-lead'),
        ],
        positions: [],
      }),
    );

    expect(tree.roots.map((node) => node.unit.id)).toEqual(['root']);
    expect(tree.roots[0]?.children.map((node) => node.unit.id)).toEqual(['delivery', 'support']);
    expect(tree.roots[0]?.children[0]?.children.map((node) => node.unit.id)).toEqual(['squad']);
    expect(tree.detachedUnits).toHaveLength(0);
    expect(tree.orphanPositions).toHaveLength(0);
  });

  it('separates the leadership position from the remaining positions of the unit', () => {
    const tree = buildOrganogramTree(
      snapshot({
        rootUnitId: 'root',
        units: [unit('root', null, 'lead')],
        positions: [position('member-a', 'root'), position('lead', 'root'), position('member-b', 'root')],
      }),
    );

    const root = tree.roots[0];
    expect(root?.leadershipPosition?.id).toBe('lead');
    expect(root?.positions.map((each) => each.id)).toEqual(['member-a', 'member-b']);
    expect(nodePositions(root!).map((each) => each.id)).toEqual(['lead', 'member-a', 'member-b']);
  });

  it('reports unresolved leadership instead of borrowing a position from another unit', () => {
    const tree = buildOrganogramTree(
      snapshot({
        rootUnitId: 'root',
        units: [unit('root', null, 'elsewhere'), unit('other', 'root', 'elsewhere')],
        positions: [position('elsewhere', 'other')],
      }),
    );

    expect(tree.roots[0]?.leadershipPosition).toBeNull();
    expect(tree.roots[0]?.children[0]?.leadershipPosition?.id).toBe('elsewhere');
    expect(countPositions(tree.roots)).toBe(1);
  });

  it('surfaces positions whose unit is absent instead of dropping them', () => {
    const tree = buildOrganogramTree(
      snapshot({
        rootUnitId: 'root',
        units: [unit('root', null, 'lead')],
        positions: [position('lead', 'root'), position('ghost', 'vanished')],
      }),
    );

    expect(tree.orphanPositions.map((each) => each.id)).toEqual(['ghost']);
    expect(countPositions(tree.roots)).toBe(1);
  });

  it('treats a unit with an unknown parent as a root, after the declared root', () => {
    const tree = buildOrganogramTree(
      snapshot({
        rootUnitId: 'root',
        units: [unit('stray', 'vanished', 'stray-lead'), unit('root', null, 'root-lead')],
        positions: [],
      }),
    );

    expect(tree.roots.map((node) => node.unit.id)).toEqual(['root', 'stray']);
  });

  it('keeps units caught in a parent cycle visible exactly once', () => {
    const tree = buildOrganogramTree(
      snapshot({
        rootUnitId: 'root',
        units: [unit('root', null, 'root-lead'), unit('a', 'b', 'a-lead'), unit('b', 'a', 'b-lead')],
        positions: [],
      }),
    );

    expect(tree.roots.map((node) => node.unit.id)).toEqual(['root']);
    expect(tree.detachedUnits.map((node) => node.unit.id)).toEqual(['a']);
    expect(tree.detachedUnits[0]?.children.map((node) => node.unit.id)).toEqual(['b']);
    expect(tree.detachedUnits[0]?.children[0]?.children).toHaveLength(0);
  });

  it('treats a unit that is its own parent as a root', () => {
    const tree = buildOrganogramTree(
      snapshot({
        rootUnitId: 'root',
        units: [unit('root', 'root', 'root-lead')],
        positions: [],
      }),
    );

    expect(tree.roots.map((node) => node.unit.id)).toEqual(['root']);
    expect(tree.detachedUnits).toHaveLength(0);
  });
});

describe('resolvePositionState', () => {
  it('prefers the live state over the one embedded in the snapshot', () => {
    const target = position('lead', 'root');
    const live = new Map<string, OrganizationPositionState>([
      ['lead', { ...target.operational_state, state: 'Working', sequence: 9 }],
    ]);

    expect(resolvePositionState(target, live).state).toBe('Working');
    expect(resolvePositionState(position('other', 'root'), live).state).toBe('Idle');
  });
});

function snapshot(input: {
  rootUnitId: string;
  units: OrganizationUnit[];
  positions: OrganizationPosition[];
}): OrganogramResponse {
  return {
    registry: { version: 7, fingerprint: 'abc123' },
    generated_at_utc: '2026-08-03T10:00:00Z',
    root_unit_id: input.rootUnitId,
    organization: {
      id: 'acme',
      name: 'Acme',
      root_unit_id: input.rootUnitId,
      root_position_id: 'root-lead',
    },
    units: input.units,
    positions: input.positions,
  };
}

function unit(id: string, parentUnitId: string | null, leadershipPositionId: string): OrganizationUnit {
  return { id, name: id, parent_unit_id: parentUnitId, leadership_position_id: leadershipPositionId };
}

function position(id: string, unitId: string): OrganizationPosition {
  return {
    id,
    name: id,
    unit_id: unitId,
    occupant: { id: `${id}-occupant`, type: 'AiAgent' },
    hierarchy: { reports_to_position_id: null, direct_subordinate_position_ids: [] },
    operational_state: {
      position_id: id,
      state: 'Idle',
      sequence: 1,
      updated_at_utc: '2026-08-03T09:59:00Z',
      last_correlated_event: null,
    },
  };
}
