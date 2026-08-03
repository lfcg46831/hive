import { describe, expect, it } from 'vitest';
import type {
  OrganizationOccupantType,
  OrganizationPosition,
  OrganizationPositionState,
  OrganizationUnit,
  OrganogramResponse,
  PositionOperationalState,
} from '../../api/index.js';
import { EMPTY_FILTER, filterOrganogram, isFilterActive } from './organogramFilter.js';
import { buildOrganogramTree } from './organogramTree.js';
import type { OrganogramTree, OrganogramUnitNode } from './organogramTree.js';

const NO_LIVE_STATES = new Map<string, OrganizationPositionState>();

describe('isFilterActive', () => {
  it('treats whitespace as no query', () => {
    expect(isFilterActive(EMPTY_FILTER)).toBe(false);
    expect(isFilterActive({ query: '   ', states: new Set() })).toBe(false);
    expect(isFilterActive({ query: 'lead', states: new Set() })).toBe(true);
    expect(isFilterActive({ query: '', states: new Set(['Blocked']) })).toBe(true);
  });
});

describe('filterOrganogram', () => {
  it('returns the very same tree when no filter is active', () => {
    const tree = buildOrganogramTree(sample());
    const result = filterOrganogram(tree, NO_LIVE_STATES, EMPTY_FILTER);

    expect(result.tree).toBe(tree);
    expect(result.active).toBe(false);
    expect(result.matchedPositions).toBe(result.totalPositions);
    expect(result.totalPositions).toBe(5);
  });

  it('keeps the ancestors of a match so the hierarchy stays readable', () => {
    const result = filterOrganogram(buildOrganogramTree(sample()), NO_LIVE_STATES, {
      query: 'squad-dev',
      states: new Set(),
    });

    expect(unitIds(result.tree)).toEqual(['root', 'delivery', 'squad']);
    expect(positionIds(result.tree)).toEqual(['squad-dev']);
    expect(result.matchedPositions).toBe(1);
    expect(result.totalPositions).toBe(5);
  });

  it('keeps every position of a unit whose own name matches', () => {
    const result = filterOrganogram(buildOrganogramTree(sample()), NO_LIVE_STATES, {
      query: 'squad',
      states: new Set(),
    });

    expect(positionIds(result.tree)).toEqual(['squad-lead', 'squad-dev']);
  });

  it('matches the occupant by identity and by kind', () => {
    const tree = buildOrganogramTree(sample());

    expect(
      positionIds(filterOrganogram(tree, NO_LIVE_STATES, { query: 'sofia', states: new Set() }).tree),
    ).toEqual(['delivery-lead']);
    expect(
      positionIds(filterOrganogram(tree, NO_LIVE_STATES, { query: 'human', states: new Set() }).tree),
    ).toEqual(['delivery-lead']);
    expect(
      positionIds(filterOrganogram(tree, NO_LIVE_STATES, { query: 'vacant', states: new Set() }).tree),
    ).toEqual(['support-lead']);
  });

  it('ignores case and diacritics', () => {
    const result = filterOrganogram(buildOrganogramTree(sample()), NO_LIVE_STATES, {
      query: 'SOFIA',
      states: new Set(),
    });

    expect(positionIds(result.tree)).toEqual(['delivery-lead']);
  });

  it('requires every term of the query to match', () => {
    const tree = buildOrganogramTree(sample());

    expect(
      positionIds(filterOrganogram(tree, NO_LIVE_STATES, { query: 'squad dev', states: new Set() }).tree),
    ).toEqual(['squad-dev']);
    expect(
      positionIds(filterOrganogram(tree, NO_LIVE_STATES, { query: 'squad ghost', states: new Set() }).tree),
    ).toEqual([]);
  });

  it('filters by the state the reader sees, not the one frozen in the snapshot', () => {
    const live = new Map<string, OrganizationPositionState>([
      ['squad-dev', liveState('squad-dev', 'Blocked')],
    ]);

    const result = filterOrganogram(buildOrganogramTree(sample()), live, {
      query: '',
      states: new Set<PositionOperationalState>(['Blocked']),
    });

    expect(positionIds(result.tree)).toEqual(['squad-dev']);
    expect(result.stateCounts.get('Blocked')).toBe(1);
    expect(result.stateCounts.get('Idle')).toBe(3);
    expect(result.stateCounts.get('Working')).toBe(1);
  });

  it('accepts a position matching any of the selected states', () => {
    const result = filterOrganogram(buildOrganogramTree(sample()), NO_LIVE_STATES, {
      query: '',
      states: new Set<PositionOperationalState>(['Working', 'Offline']),
    });

    expect(positionIds(result.tree)).toEqual(['support-lead']);
    expect(result.stateCounts.get('Offline')).toBe(0);
  });

  it('intersects the query with the state filter', () => {
    const result = filterOrganogram(buildOrganogramTree(sample()), NO_LIVE_STATES, {
      query: 'squad',
      states: new Set<PositionOperationalState>(['Working']),
    });

    expect(unitIds(result.tree)).toEqual([]);
    expect(result.matchedPositions).toBe(0);
  });

  it('keeps a matching unit that holds no position, but not under a state filter', () => {
    const snapshot = sample();
    snapshot.units.push(unit('lab', 'root', 'lab-lead'));

    const byName = filterOrganogram(buildOrganogramTree(snapshot), NO_LIVE_STATES, {
      query: 'lab',
      states: new Set(),
    });
    expect(unitIds(byName.tree)).toEqual(['root', 'lab']);

    const byState = filterOrganogram(buildOrganogramTree(snapshot), NO_LIVE_STATES, {
      query: 'lab',
      states: new Set<PositionOperationalState>(['Idle']),
    });
    expect(unitIds(byState.tree)).toEqual([]);
  });

  it('drops the leadership position when it does not match', () => {
    const result = filterOrganogram(buildOrganogramTree(sample()), NO_LIVE_STATES, {
      query: 'squad-dev',
      states: new Set(),
    });

    const squad = findUnit(result.tree, 'squad');
    expect(squad?.leadershipPosition).toBeNull();
    expect(squad?.positions.map((each) => each.id)).toEqual(['squad-dev']);
  });

  it('filters detached units and positions without a unit instead of skipping them', () => {
    const snapshot = sample();
    snapshot.units.push(unit('cycle-a', 'cycle-b', 'cycle-a-lead'), unit('cycle-b', 'cycle-a', ''));
    snapshot.positions.push(position('cycle-a-lead', 'cycle-a'), position('ghost', 'vanished'));

    const tree = buildOrganogramTree(snapshot);
    expect(filterOrganogram(tree, NO_LIVE_STATES, EMPTY_FILTER).totalPositions).toBe(7);

    const detached = filterOrganogram(tree, NO_LIVE_STATES, {
      query: 'cycle',
      states: new Set(),
    });
    expect(detached.tree.roots).toHaveLength(0);
    expect(detached.tree.detachedUnits.map((node) => node.unit.id)).toEqual(['cycle-a']);
    expect(detached.matchedPositions).toBe(1);

    const orphan = filterOrganogram(tree, NO_LIVE_STATES, { query: 'ghost', states: new Set() });
    expect(orphan.tree.orphanPositions.map((each) => each.id)).toEqual(['ghost']);
    expect(orphan.tree.roots).toHaveLength(0);
  });

  it('counts states over the whole organization, not over the filtered view', () => {
    const result = filterOrganogram(buildOrganogramTree(sample()), NO_LIVE_STATES, {
      query: 'squad-dev',
      states: new Set(),
    });

    expect(result.matchedPositions).toBe(1);
    expect(result.stateCounts.get('Idle')).toBe(4);
    expect(result.stateCounts.get('Working')).toBe(1);
  });
});

/**
 * root
 *  ├── delivery (delivery-lead, human Sofía)
 *  │    └── squad (squad-lead, squad-dev)
 *  └── support (support-lead, vacant, Working)
 */
function sample(): OrganogramResponse {
  return snapshot({
    rootUnitId: 'root',
    units: [
      unit('root', null, 'root-lead'),
      unit('delivery', 'root', 'delivery-lead'),
      unit('squad', 'delivery', 'squad-lead'),
      unit('support', 'root', 'support-lead'),
    ],
    positions: [
      position('root-lead', 'root'),
      position('delivery-lead', 'delivery', { occupantId: 'Sofía Sousa', occupantType: 'Human' }),
      position('squad-lead', 'squad'),
      position('squad-dev', 'squad'),
      position('support-lead', 'support', { occupantId: null, state: 'Working' }),
    ],
  });
}

function unitIds(tree: OrganogramTree): string[] {
  const ids: string[] = [];
  const walk = (nodes: readonly OrganogramUnitNode[]): void => {
    for (const node of nodes) {
      ids.push(node.unit.id);
      walk(node.children);
    }
  };

  walk(tree.roots);
  walk(tree.detachedUnits);
  return ids;
}

function positionIds(tree: OrganogramTree): string[] {
  const ids: string[] = [];
  const walk = (nodes: readonly OrganogramUnitNode[]): void => {
    for (const node of nodes) {
      if (node.leadershipPosition !== null) {
        ids.push(node.leadershipPosition.id);
      }

      ids.push(...node.positions.map((each) => each.id));
      walk(node.children);
    }
  };

  walk(tree.roots);
  walk(tree.detachedUnits);
  ids.push(...tree.orphanPositions.map((each) => each.id));
  return ids;
}

function findUnit(tree: OrganogramTree, unitId: string): OrganogramUnitNode | null {
  const walk = (nodes: readonly OrganogramUnitNode[]): OrganogramUnitNode | null => {
    for (const node of nodes) {
      if (node.unit.id === unitId) {
        return node;
      }

      const found = walk(node.children);
      if (found !== null) {
        return found;
      }
    }

    return null;
  };

  return walk(tree.roots) ?? walk(tree.detachedUnits);
}

function liveState(
  positionId: string,
  state: PositionOperationalState,
): OrganizationPositionState {
  return {
    position_id: positionId,
    state,
    sequence: 4,
    updated_at_utc: '2026-08-03T10:01:00Z',
    last_correlated_event: null,
  };
}

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

function position(
  id: string,
  unitId: string,
  options: {
    occupantId?: string | null;
    occupantType?: OrganizationOccupantType;
    state?: PositionOperationalState;
  } = {},
): OrganizationPosition {
  return {
    id,
    name: id,
    unit_id: unitId,
    occupant: {
      id: options.occupantId === undefined ? `${id}-occupant` : options.occupantId,
      type: options.occupantType ?? 'AiAgent',
    },
    hierarchy: { reports_to_position_id: null, direct_subordinate_position_ids: [] },
    operational_state: {
      position_id: id,
      state: options.state ?? 'Idle',
      sequence: 1,
      updated_at_utc: '2026-08-03T09:59:00Z',
      last_correlated_event: null,
    },
  };
}
