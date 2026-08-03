import type { OrganizationPositionState } from '../../api/index.js';
import { PositionCard } from './PositionCard.js';
import type { OrganogramUnitNode } from './organogramTree.js';
import { nodePositions, resolvePositionState } from './organogramTree.js';

export interface UnitBranchProps {
  readonly node: OrganogramUnitNode;
  readonly liveStates: ReadonlyMap<string, OrganizationPositionState>;
  readonly depth?: number;
}

/** One unit and, recursively, the units under it. */
export function UnitBranch({ node, liveStates, depth = 0 }: UnitBranchProps) {
  const positions = nodePositions(node);
  const leadershipMissing =
    node.leadershipPosition === null && node.unit.leadership_position_id.length > 0;

  return (
    <li className="unit" data-unit-id={node.unit.id} data-depth={depth}>
      <section className="unit__body">
        <header className="unit__header">
          <h3 className="unit__name">{node.unit.name ?? node.unit.id}</h3>
          <span className="unit__id">{node.unit.id}</span>
        </header>

        {leadershipMissing ? (
          <p className="unit__warning">
            Declared leadership <code>{node.unit.leadership_position_id}</code> is not a position of
            this unit.
          </p>
        ) : null}

        {positions.length === 0 ? (
          <p className="unit__empty">No positions in this unit.</p>
        ) : (
          <ul className="position-list">
            {positions.map((position) => (
              <PositionCard
                key={position.id}
                position={position}
                state={resolvePositionState(position, liveStates)}
                isLeadership={position === node.leadershipPosition}
              />
            ))}
          </ul>
        )}
      </section>

      {node.children.length > 0 ? (
        <ul className="unit-list">
          {node.children.map((child) => (
            <UnitBranch key={child.unit.id} node={child} liveStates={liveStates} depth={depth + 1} />
          ))}
        </ul>
      ) : null}
    </li>
  );
}
