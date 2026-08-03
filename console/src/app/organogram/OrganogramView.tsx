import { useMemo } from 'react';
import type { OrganizationPositionState, OrganogramResponse } from '../../api/index.js';
import { formatUtc } from '../format.js';
import type { ConsoleFreshness } from '../status/consoleStatus.js';
import { isEmptySnapshot } from '../status/consoleStatus.js';
import { EmptyPanel } from '../status/StatusPanels.js';
import { PositionCard } from './PositionCard.js';
import { UnitBranch } from './UnitBranch.js';
import { UpdateIndicator } from './UpdateIndicator.js';
import { buildOrganogramTree, countPositions, resolvePositionState } from './organogramTree.js';
import type { UpdateChannel } from './useOrganogramLiveView.js';

export interface OrganogramViewProps {
  readonly snapshot: OrganogramResponse;
  readonly liveStates: ReadonlyMap<string, OrganizationPositionState>;
  readonly channel: UpdateChannel;
  readonly freshness: ConsoleFreshness;
  readonly lastSyncedAtUtc: string | null;
  readonly projectionAppliedAtUtc: string | null;
  readonly registryUpdating: boolean;
  readonly refreshing: boolean;
}

/**
 * The living organogram: units, positions, occupants, leadership and the
 * operational state of each position, over a snapshot that only ever comes from
 * the public API. There is no editing affordance anywhere in this subtree.
 */
export function OrganogramView({
  snapshot,
  liveStates,
  channel,
  freshness,
  lastSyncedAtUtc,
  projectionAppliedAtUtc,
  registryUpdating,
  refreshing,
}: OrganogramViewProps) {
  const tree = useMemo(() => buildOrganogramTree(snapshot), [snapshot]);
  const positionCount = useMemo(
    () => countPositions(tree.roots) + countPositions(tree.detachedUnits) + tree.orphanPositions.length,
    [tree],
  );

  return (
    <section className="organogram" aria-label="Organogram">
      <header className="organogram__header">
        <div>
          <h2 className="organogram__title">{snapshot.organization.name ?? snapshot.organization.id}</h2>
          <p className="organogram__summary">
            {snapshot.units.length} units · {positionCount} positions · snapshot generated{' '}
            {formatUtc(snapshot.generated_at_utc)}
          </p>
        </div>
        <UpdateIndicator
          channel={channel}
          freshness={freshness}
          lastSyncedAtUtc={lastSyncedAtUtc}
          projectionAppliedAtUtc={projectionAppliedAtUtc}
          registry={snapshot.registry}
          registryUpdating={registryUpdating}
          refreshing={refreshing}
        />
      </header>

      {isEmptySnapshot(snapshot) ? (
        <EmptyPanel
          organizationId={snapshot.organization.id}
          registryVersion={snapshot.registry.version}
        />
      ) : null}

      {!isEmptySnapshot(snapshot) && tree.roots.length === 0 && tree.detachedUnits.length === 0 ? (
        <p className="organogram__empty">This organization has no units in the current registry.</p>
      ) : null}

      {tree.roots.length === 0 ? null : (
        <ul className="unit-list unit-list--root">
          {tree.roots.map((node) => (
            <UnitBranch key={node.unit.id} node={node} liveStates={liveStates} />
          ))}
        </ul>
      )}

      {tree.detachedUnits.length > 0 ? (
        <section className="organogram__detached" aria-label="Detached units">
          <h3>Units outside the hierarchy</h3>
          <p className="organogram__detached-note">
            These units are unreachable from the root because their parent is missing from the
            snapshot or forms a cycle.
          </p>
          <ul className="unit-list unit-list--root">
            {tree.detachedUnits.map((node) => (
              <UnitBranch key={node.unit.id} node={node} liveStates={liveStates} />
            ))}
          </ul>
        </section>
      ) : null}

      {tree.orphanPositions.length > 0 ? (
        <section className="organogram__detached" aria-label="Positions without a unit">
          <h3>Positions without a unit</h3>
          <ul className="position-list">
            {tree.orphanPositions.map((position) => (
              <PositionCard
                key={position.id}
                position={position}
                state={resolvePositionState(position, liveStates)}
                isLeadership={false}
              />
            ))}
          </ul>
        </section>
      ) : null}
    </section>
  );
}
