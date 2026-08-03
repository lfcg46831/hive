/**
 * Client-side narrowing of the organogram already on screen.
 *
 * The filters operate over the loaded snapshot and add no endpoint: narrowing
 * the view never changes what was asked of the API, so there is no second
 * source of truth for who exists and no way for a filter to make the console
 * disagree with the organization. Everything here is presentation — no
 * organizational fact is derived, invented or reordered.
 *
 * Four properties matter for a view meant to stay navigable in an organization
 * larger than the F0 example:
 *  - hierarchy survives filtering: a unit is kept when a descendant matches, so
 *    a match is always read in its place in the structure and never as a flat
 *    list that hides who it reports to;
 *  - the state filter reads the state the reader sees, live updates included,
 *    rather than the one frozen in the snapshot;
 *  - what the unfiltered view surfaces on purpose is filtered, never skipped:
 *    detached units and positions without a unit obey the same rules, so a
 *    filter cannot quietly hide a broken snapshot;
 *  - a unit kept only for context is distinguishable from a unit that genuinely
 *    holds no positions, because the view must not claim the second when it
 *    means the first.
 */

import type {
  OrganizationOccupant,
  OrganizationPosition,
  OrganizationPositionState,
  OrganizationUnit,
  PositionOperationalState,
} from '../../api/index.js';
import type { OrganogramTree, OrganogramUnitNode } from './organogramTree.js';
import { countPositions, nodePositions, resolvePositionState } from './organogramTree.js';

/** Operational states in the precedence order the API resolves them by. */
export const FILTERABLE_STATES: readonly PositionOperationalState[] = [
  'Offline',
  'Blocked',
  'WaitingHuman',
  'Working',
  'Idle',
];

export interface OrganogramFilter {
  /** Free text matched against unit, position and occupant identity. */
  readonly query: string;
  /** Selected states; empty means every state is accepted. */
  readonly states: ReadonlySet<PositionOperationalState>;
}

export interface OrganogramFilterResult {
  /** The tree to render: the input tree when no filter is active. */
  readonly tree: OrganogramTree;
  readonly active: boolean;
  readonly matchedPositions: number;
  readonly totalPositions: number;
  /** Positions per state over the *unfiltered* tree, for the filter controls. */
  readonly stateCounts: ReadonlyMap<PositionOperationalState, number>;
}

export const EMPTY_FILTER: OrganogramFilter = { query: '', states: new Set() };

export function isFilterActive(filter: OrganogramFilter): boolean {
  return filter.query.trim().length > 0 || filter.states.size > 0;
}

/**
 * Narrows the tree to the positions matching the filter, keeping every ancestor
 * of a kept position. An inactive filter returns the input tree unchanged, so
 * the common case costs nothing and renders the exact same objects.
 */
export function filterOrganogram(
  tree: OrganogramTree,
  liveStates: ReadonlyMap<string, OrganizationPositionState>,
  filter: OrganogramFilter,
): OrganogramFilterResult {
  const stateCounts = countByState(tree, liveStates);
  const totalPositions = countTreePositions(tree);

  if (!isFilterActive(filter)) {
    return { tree, active: false, matchedPositions: totalPositions, totalPositions, stateCounts };
  }

  const terms = toTerms(filter.query);
  const keepPosition = (position: OrganizationPosition, unitMatches: boolean): boolean =>
    (unitMatches || matchesTerms(positionHaystack(position), terms)) &&
    matchesState(position, liveStates, filter.states);

  const filterNode = (node: OrganogramUnitNode): OrganogramUnitNode | null => {
    const unitMatches = matchesTerms(unitHaystack(node.unit), terms);
    const leadershipPosition =
      node.leadershipPosition !== null && keepPosition(node.leadershipPosition, unitMatches)
        ? node.leadershipPosition
        : null;
    const positions = node.positions.filter((position) => keepPosition(position, unitMatches));
    const children = node.children
      .map(filterNode)
      .filter((child): child is OrganogramUnitNode => child !== null);

    const kept = positions.length + (leadershipPosition === null ? 0 : 1);
    if (kept > 0 || children.length > 0) {
      return { unit: node.unit, leadershipPosition, positions, children };
    }

    // A unit named by the query but holding no position at all is still an
    // answer to that query, as long as no state was demanded of it — a state
    // filter is a question about positions, which this unit cannot answer.
    return unitMatches && terms.length > 0 && filter.states.size === 0
      ? { unit: node.unit, leadershipPosition: null, positions: [], children: [] }
      : null;
  };

  const filtered: OrganogramTree = {
    roots: tree.roots.map(filterNode).filter(isNode),
    detachedUnits: tree.detachedUnits.map(filterNode).filter(isNode),
    orphanPositions: tree.orphanPositions.filter((position) => keepPosition(position, false)),
  };

  return {
    tree: filtered,
    active: true,
    matchedPositions: countTreePositions(filtered),
    totalPositions,
    stateCounts,
  };
}

/** Every position of the tree, wherever it hangs, including orphans. */
export function* eachPosition(tree: OrganogramTree): Generator<OrganizationPosition> {
  yield* eachNodePosition(tree.roots);
  yield* eachNodePosition(tree.detachedUnits);
  yield* tree.orphanPositions;
}

function* eachNodePosition(nodes: readonly OrganogramUnitNode[]): Generator<OrganizationPosition> {
  for (const node of nodes) {
    yield* nodePositions(node);
    yield* eachNodePosition(node.children);
  }
}

/** Positions of the whole tree, counted the same way the header counts them. */
export function countTreePositions(tree: OrganogramTree): number {
  return (
    countPositions(tree.roots) + countPositions(tree.detachedUnits) + tree.orphanPositions.length
  );
}

function countByState(
  tree: OrganogramTree,
  liveStates: ReadonlyMap<string, OrganizationPositionState>,
): ReadonlyMap<PositionOperationalState, number> {
  const counts = new Map<PositionOperationalState, number>(
    FILTERABLE_STATES.map((state) => [state, 0]),
  );

  for (const position of eachPosition(tree)) {
    const state = resolvePositionState(position, liveStates).state;
    counts.set(state, (counts.get(state) ?? 0) + 1);
  }

  return counts;
}

function matchesState(
  position: OrganizationPosition,
  liveStates: ReadonlyMap<string, OrganizationPositionState>,
  states: ReadonlySet<PositionOperationalState>,
): boolean {
  return states.size === 0 || states.has(resolvePositionState(position, liveStates).state);
}

/**
 * Terms are AND-ed, so `delivery lead` finds the lead of delivery instead of
 * everything mentioning either word. Order between terms is irrelevant, which
 * matters when the reader knows the two words but not how the registry spells
 * the identifier.
 */
function toTerms(query: string): readonly string[] {
  return normalize(query).split(/\s+/).filter((term) => term.length > 0);
}

function matchesTerms(haystack: string, terms: readonly string[]): boolean {
  return terms.every((term) => haystack.includes(term));
}

function unitHaystack(unit: OrganizationUnit): string {
  return normalize([unit.id, unit.name].join(' '));
}

function positionHaystack(position: OrganizationPosition): string {
  return normalize(
    [position.id, position.name, position.unit_id, ...occupantTokens(position.occupant)].join(' '),
  );
}

/**
 * The occupant is searchable by identity and by kind, so `human` narrows to the
 * people in the organization and a vacant position is reachable by the word the
 * view itself uses for it.
 */
function occupantTokens(occupant: OrganizationOccupant): readonly string[] {
  const kind = occupant.type === 'Human' ? 'human' : 'ai agent';
  return occupant.id === null ? [kind, 'vacant'] : [occupant.id, kind];
}

/**
 * Case- and accent-insensitive, because the reader types `sousa` for `Sousa`
 * and `Sofia` for `Sofía` and means the same person either way.
 */
function normalize(value: string | null): string {
  return (value ?? '')
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase();
}

function isNode(node: OrganogramUnitNode | null): node is OrganogramUnitNode {
  return node !== null;
}
