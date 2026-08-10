import { useEffect, useRef, useState } from 'react';
import type { ConsoleConfig } from '../../config.js';
import { formatUtc } from '../format.js';
import { NoticeList } from '../status/NoticeList.js';
import { FailurePanel, LoadingPanel } from '../status/StatusPanels.js';
import { useNowMs } from '../status/useNowMs.js';
import { InboxFilterBar } from './InboxFilterBar.js';
import { InboxItemDetail } from './InboxItemDetail.js';
import { InboxList } from './InboxList.js';
import type { InboxFilter } from './inboxFilter.js';
import { EMPTY_INBOX_FILTER, isInboxFilterActive } from './inboxFilter.js';
import { indexOfSelection, resolveInboxSelection } from './inboxSelection.js';
import { deriveInboxStatus } from './inboxStatus.js';
import { useInboxItemDetail } from './useInboxItemDetail.js';
import { useInboxLiveView } from './useInboxLiveView.js';

/** How often ages and deadline badges are recomputed. */
const TICK_MS = 1_000;

/**
 * The human inbox: what is addressed to the positions this person occupies, and
 * the two things they can do about it — respond as the position, or decide an
 * approval the policy assigned to them.
 *
 * Deadlines make this view time-dependent in a way the organogram is not, so the
 * clock runs even on a live subscription: an item whose deadline passes while
 * nothing arrives from the server still has to stop looking scheduled.
 */
export function InboxView({ config }: { readonly config: ConsoleConfig }) {
  const [filter, setFilter] = useState<InboxFilter>(EMPTY_INBOX_FILTER);
  const [selectedItemId, setSelectedItemId] = useState<string | null>(null);
  const view = useInboxLiveView(config, filter);
  const nowMs = useNowMs(TICK_MS, true);

  // Where the selection sat in the list the reader last saw. Once an item is
  // gone the list can no longer answer that, and succession would collapse into
  // "back to the top".
  const selectedIndexRef = useRef(0);

  // The selection invariant, re-applied to every list the view receives: first
  // load, filter change, manual refresh, poll and applied realtime update all
  // arrive here as a new `items`.
  useEffect(() => {
    const resolved = resolveInboxSelection({
      items: view.items,
      selectedItemId,
      lastIndex: selectedIndexRef.current,
    });

    if (resolved !== selectedItemId) {
      setSelectedItemId(resolved);
      return;
    }

    const index = indexOfSelection(view.items, resolved);
    if (index >= 0) {
      selectedIndexRef.current = index;
    }
  }, [selectedItemId, view.items]);

  // A committed action changes what the list shows, so the list refetches; the
  // detail never edits the derived state it was given.
  const detail = useInboxItemDetail(config, selectedItemId, view.refresh);

  const status = deriveInboxStatus({
    phase: view.phase,
    error: view.error,
    loaded: view.loaded,
    itemCount: view.items.length,
    channel: view.channel,
    lastSyncedAtUtc: view.lastSyncedAtUtc,
    projectionAppliedAtUtc: view.projectionAppliedAtUtc,
    pendingUpdate: view.pendingUpdate,
    missedNotifications: view.missedNotifications,
    pollIntervalMs: config.pollIntervalMs,
    nowMs,
  });
  const projectionMayBeIncomplete =
    view.projectionAppliedAtUtc === null ||
    status.freshness.level === 'unknown' ||
    status.freshness.level === 'stale';

  return (
    <section className="inbox" aria-label="Inbox" data-stage={status.stage}>
      <header className="inbox__header">
        <div>
          <h2 className="organogram__title">Inbox</h2>
          <p className="organogram__summary">
            {view.items.length} item{view.items.length === 1 ? '' : 's'}
            {view.hasMore ? ' loaded' : ''} · {status.freshness.label}
            {view.generatedAtUtc === null ? '' : ` · snapshot ${formatUtc(view.generatedAtUtc)}`}
          </p>
          {view.projectionAppliedAtUtc === null ? null : (
            <p className="organogram__summary">
              Projection applied up to {formatUtc(view.projectionAppliedAtUtc)}.
            </p>
          )}
        </div>
        <button
          type="button"
          className="filters__clear"
          disabled={view.refreshing}
          onClick={view.refresh}
        >
          {view.refreshing ? 'Refreshing…' : 'Refresh'}
        </button>
      </header>

      <NoticeList notices={status.notices} onRetry={view.refresh} />

      {status.stage === 'loading' ? <LoadingPanel /> : null}

      {status.stage === 'failed' && status.failure !== null ? (
        <FailurePanel failure={status.failure} onRetry={view.refresh} />
      ) : null}

      {view.loaded ? (
        <InboxFilterBar filter={filter} disabled={view.refreshing} onChange={setFilter} />
      ) : null}

      {status.stage === 'empty' ? (
        <div className="panel" role="status">
          <p className="panel__title">
            {projectionMayBeIncomplete
              ? 'Inbox data may be incomplete'
              : isInboxFilterActive(filter)
                ? 'No item matches these filters'
                : 'Your inbox is empty'}
          </p>
          <p className="panel__detail">
            {projectionMayBeIncomplete
              ? 'The projection has not reported a recent applied event, so this empty result is not treated as an organizational fact.'
              : isInboxFilterActive(filter)
              ? 'The filters are applied by the API, so this is the whole matching inbox and not an exhausted page.'
              : 'Nothing is currently addressed to the positions you occupy as of the projection watermark shown above. Items appear here as the organization routes them.'}
          </p>
        </div>
      ) : null}

      {status.stage === 'ready' ? (
        <div className="inbox__body">
          <div className="inbox__column">
            <InboxList
              items={view.items}
              selectedItemId={selectedItemId}
              nowMs={nowMs}
              onSelect={setSelectedItemId}
            />
            {view.hasMore ? (
              <button
                type="button"
                className="filters__clear"
                disabled={view.loadingMore}
                onClick={view.loadMore}
              >
                {view.loadingMore ? 'Loading…' : 'Load more'}
              </button>
            ) : null}
          </div>

          <InboxItemDetail detail={detail} nowMs={nowMs} />
        </div>
      ) : null}
    </section>
  );
}
