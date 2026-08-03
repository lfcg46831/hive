/**
 * Derivation of the renderable organogram tree from the flat public snapshot.
 *
 * Two rules govern this module. First, ordering is never invented: the API
 * returns collections in a deterministic order derived from the registry's
 * stable keys, so units and positions keep the order in which they arrive.
 * Second, nothing is silently dropped: a position whose unit is absent, a unit
 * whose parent is absent and a unit caught in a parent cycle are all surfaced
 * as detached nodes rather than disappearing from a view whose whole purpose is
 * to show who exists.
 */

import type {
  OrganizationPosition,
  OrganizationPositionState,
  OrganizationUnit,
  OrganogramResponse,
} from '../../api/index.js';

export interface OrganogramUnitNode {
  readonly unit: OrganizationUnit;
  /**
   * Leadership resolved among this unit's own positions. Null when
   * `leadership_position_id` names nothing the unit holds, which the view
   * reports instead of hiding; a position is never rendered under a unit that
   * does not declare it.
   */
  readonly leadershipPosition: OrganizationPosition | null;
  /** Positions of this unit, leadership excluded, in snapshot order. */
  readonly positions: readonly OrganizationPosition[];
  readonly children: readonly OrganogramUnitNode[];
}

export interface OrganogramTree {
  readonly roots: readonly OrganogramUnitNode[];
  /**
   * Units reachable from no root — an unknown parent or a parent cycle. Shown
   * apart so a broken snapshot is visible instead of invisible.
   */
  readonly detachedUnits: readonly OrganogramUnitNode[];
  /** Positions whose `unit_id` is absent from the snapshot. */
  readonly orphanPositions: readonly OrganizationPosition[];
}

export function buildOrganogramTree(snapshot: OrganogramResponse): OrganogramTree {
  const unitsById = new Map(snapshot.units.map((unit) => [unit.id, unit]));

  const positionsByUnit = new Map<string, OrganizationPosition[]>();
  const orphanPositions: OrganizationPosition[] = [];
  for (const position of snapshot.positions) {
    if (!unitsById.has(position.unit_id)) {
      orphanPositions.push(position);
      continue;
    }

    appendTo(positionsByUnit, position.unit_id, position);
  }

  const childUnits = new Map<string, OrganizationUnit[]>();
  const rootUnits: OrganizationUnit[] = [];
  for (const unit of snapshot.units) {
    const parentId = unit.parent_unit_id;
    if (parentId === null || parentId === unit.id || !unitsById.has(parentId)) {
      rootUnits.push(unit);
      continue;
    }

    appendTo(childUnits, parentId, unit);
  }

  const visited = new Set<string>();

  function toNode(unit: OrganizationUnit): OrganogramUnitNode {
    visited.add(unit.id);
    const unitPositions = positionsByUnit.get(unit.id) ?? [];
    const leadershipPosition =
      unitPositions.find((position) => position.id === unit.leadership_position_id) ?? null;

    return {
      unit,
      leadershipPosition,
      positions: unitPositions.filter((position) => position !== leadershipPosition),
      children: (childUnits.get(unit.id) ?? [])
        .filter((child) => !visited.has(child.id))
        .map(toNode),
    };
  }

  const roots = orderRootsFirst(rootUnits, snapshot.root_unit_id).map(toNode);

  // Whatever the traversal never reached sits in a parent cycle. Keep it
  // visible, in snapshot order, entering each cycle exactly once.
  const detachedUnits: OrganogramUnitNode[] = [];
  for (const unit of snapshot.units) {
    if (!visited.has(unit.id)) {
      detachedUnits.push(toNode(unit));
    }
  }

  return { roots, detachedUnits, orphanPositions };
}

/** Resolves the state to render, preferring live updates over the snapshot. */
export function resolvePositionState(
  position: OrganizationPosition,
  liveStates: ReadonlyMap<string, OrganizationPositionState>,
): OrganizationPositionState {
  return liveStates.get(position.id) ?? position.operational_state;
}

/** Every position of a node, leadership first, in render order. */
export function nodePositions(node: OrganogramUnitNode): readonly OrganizationPosition[] {
  return node.leadershipPosition === null
    ? node.positions
    : [node.leadershipPosition, ...node.positions];
}

export function countPositions(nodes: readonly OrganogramUnitNode[]): number {
  return nodes.reduce(
    (total, node) => total + nodePositions(node).length + countPositions(node.children),
    0,
  );
}

/**
 * The declared root unit leads the render; the rest keep snapshot order. This
 * only matters when a snapshot exposes more than one top-level unit.
 */
function orderRootsFirst(
  rootUnits: readonly OrganizationUnit[],
  rootUnitId: string,
): readonly OrganizationUnit[] {
  const declaredRoot = rootUnits.find((unit) => unit.id === rootUnitId);
  if (declaredRoot === undefined) {
    return rootUnits;
  }

  return [declaredRoot, ...rootUnits.filter((unit) => unit !== declaredRoot)];
}

function appendTo<T>(map: Map<string, T[]>, key: string, value: T): void {
  const bucket = map.get(key);
  if (bucket === undefined) {
    map.set(key, [value]);
  } else {
    bucket.push(value);
  }
}
