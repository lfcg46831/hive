import { useDeferredValue, useMemo, useState } from 'react';
import type { OrganizationPositionState, OrganogramResponse } from '../../api/index.js';
import { formatUtc } from '../format.js';
import type { ConsoleFreshness } from '../status/consoleStatus.js';
import { isEmptySnapshot } from '../status/consoleStatus.js';
import { EmptyPanel } from '../status/StatusPanels.js';
import { OrganogramFilterBar } from './OrganogramFilterBar.js';
import { PositionCard } from './PositionCard.js';
import { UnitBranch } from './UnitBranch.js';
import { UpdateIndicator } from './UpdateIndicator.js';
import type { OrganogramFilter } from './organogramFilter.js';
import { EMPTY_FILTER, filterOrganogram } from './organogramFilter.js';
import { buildOrganogramTree, resolvePositionState } from './organogramTree.js';
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
  const [filter, setFilter] = useState<OrganogramFilter>(EMPTY_FILTER);
  // Typing stays responsive on a large organization: the keystroke lands
  // immediately, the re-filtered tree follows at React's convenience.
  const deferredFilter = useDeferredValue(filter);

  const tree = useMemo(() => buildOrganogramTree(snapshot), [snapshot]);
  const result = useMemo(
    () => filterOrganogram(tree, liveStates, deferredFilter),
    [tree, liveStates, deferredFilter],
  );
  const view = result.tree;

  return (
    <section className="organogram" aria-label="Organogram">
      <header className="organogram__header">
        <div>
          <h2 className="organogram__title">{snapshot.organization.name ?? snapshot.organization.id}</h2>
          <p className="organogram__summary">
            {snapshot.units.length} units · {result.totalPositions} positions · snapshot generated{' '}
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

      {isEmptySnapshot(snapshot) ? null : (
        <OrganogramFilterBar filter={filter} result={result} onChange={setFilter} />
      )}

      {!isEmptySnapshot(snapshot) &&
      !result.active &&
      view.roots.length === 0 &&
      view.detachedUnits.length === 0 ? (
        <p className="organogram__empty">This organization has no units in the current registry.</p>
      ) : null}

      {view.roots.length === 0 ? null : (
        <ul className="unit-list unit-list--root">
          {view.roots.map((node) => (
            <UnitBranch
              key={node.unit.id}
              node={node}
              liveStates={liveStates}
              filtered={result.active}
            />
          ))}
        </ul>
      )}

      {view.detachedUnits.length > 0 ? (
        <section className="organogram__detached" aria-label="Detached units">
          <h3>Units outside the hierarchy</h3>
          <p className="organogram__detached-note">
            These units are unreachable from the root because their parent is missing from the
            snapshot or forms a cycle.
          </p>
          <ul className="unit-list unit-list--root">
            {view.detachedUnits.map((node) => (
              <UnitBranch
                key={node.unit.id}
                node={node}
                liveStates={liveStates}
                filtered={result.active}
              />
            ))}
          </ul>
        </section>
      ) : null}

      {view.orphanPositions.length > 0 ? (
        <section className="organogram__detached" aria-label="Positions without a unit">
          <h3>Positions without a unit</h3>
          <ul className="position-list">
            {view.orphanPositions.map((position) => (
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
