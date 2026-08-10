/**
 * Data layer of the inbox list.
 *
 * Same contract as the organogram view: the REST snapshot is the only source of
 * truth, the hub is an optimization, and every subscription, reconnection or
 * notification sends the view back to `/inbox`. The inbox notification carries no
 * item payload at all, which makes that rule impossible to bend accidentally.
 *
 * The one behaviour that has no counterpart in the organogram is paging. Once a
 * reader has asked for more than the first page, an incoming change must not
 * silently rebuild the list under them, so the view holds the update and says it
 * is holding it instead of applying it.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { InboxItem, OrganizationUpdatesStatus } from '../../api/index.js';
import {
  createHiveApiClient,
  createOrganizationUpdatesClient,
  hasInboxSequenceGap,
  isNewerInboxNotification,
} from '../../api/index.js';
import type { ConsoleConfig } from '../../config.js';
import type { UpdateChannel } from '../organogram/useOrganogramLiveView.js';
import type { InboxFilter } from './inboxFilter.js';
import { toInboxQuery } from './inboxFilter.js';

/** Items requested per page. The API caps this at 100. */
export const INBOX_PAGE_SIZE = 25;

export interface InboxLiveView {
  readonly phase: 'loading' | 'ready' | 'failed';
  readonly error: Error | null;
  readonly items: readonly InboxItem[];
  readonly generatedAtUtc: string | null;
  /** Timestamp of the last event applied by the server-side projection. */
  readonly projectionAppliedAtUtc: string | null;
  /** True once the first page has been answered, even when it holds no items. */
  readonly loaded: boolean;
  readonly channel: UpdateChannel;
  readonly lastSyncedAtUtc: string | null;
  readonly refreshing: boolean;
  readonly loadingMore: boolean;
  readonly hasMore: boolean;
  /** A committed change is known but withheld because the reader has paged. */
  readonly pendingUpdate: boolean;
  /** Notifications were missed; the next snapshot is a recovery, not an update. */
  readonly missedNotifications: boolean;
  loadMore(): void;
  refresh(): void;
}

export function useInboxLiveView(config: ConsoleConfig, filter: InboxFilter): InboxLiveView {
  const client = useMemo(
    () => createHiveApiClient({ baseUrl: config.apiBaseUrl, token: config.token }),
    [config.apiBaseUrl, config.token],
  );
  // The filter is held in state by the view, so its identity changes exactly
  // when the query does — which is also exactly when the ETag and the cursor
  // stop applying.
  const query = useMemo(() => toInboxQuery(filter, INBOX_PAGE_SIZE), [filter]);

  const [phase, setPhase] = useState<'loading' | 'ready' | 'failed'>('loading');
  const [error, setError] = useState<Error | null>(null);
  const [items, setItems] = useState<readonly InboxItem[]>([]);
  const [generatedAtUtc, setGeneratedAtUtc] = useState<string | null>(null);
  const [projectionAppliedAtUtc, setProjectionAppliedAtUtc] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [channel, setChannel] = useState<UpdateChannel>('connecting');
  const [lastSyncedAtUtc, setLastSyncedAtUtc] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [pendingUpdate, setPendingUpdate] = useState(false);
  const [missedNotifications, setMissedNotifications] = useState(false);
  const [refreshToken, setRefreshToken] = useState(0);

  const etagRef = useRef<string | null>(null);
  const pagesRef = useRef(1);
  const sequenceRef = useRef<number | null>(null);
  // A gap was seen and the snapshot that recovers from it is on its way.
  const recoveringGapRef = useRef(false);

  const refresh = useCallback(() => {
    setPendingUpdate(false);
    setRefreshToken((token) => token + 1);
  }, []);

  /**
   * A committed change arrived. Applying it means rebuilding the list from the
   * first page, which is only acceptable while the reader is still on it.
   */
  const invalidate = useCallback(() => {
    if (pagesRef.current > 1) {
      setPendingUpdate(true);
      return;
    }

    refresh();
  }, [refresh]);

  // Authoritative first page. Re-runs on filter change and on every event that
  // invalidates what the view holds.
  useEffect(() => {
    const abort = new AbortController();
    let cancelled = false;
    setRefreshing(true);
    etagRef.current = null;
    pagesRef.current = 1;

    void (async () => {
      try {
        const result = await client.listInbox(config.organizationId, query, {
          signal: abort.signal,
        });
        if (cancelled || result.status === 'not-modified') {
          return;
        }

        etagRef.current = result.etag;
        setItems(result.snapshot.items);
        setNextCursor(result.snapshot.next_cursor);
        setGeneratedAtUtc(result.snapshot.generated_at_utc);
        setProjectionAppliedAtUtc(result.snapshot.last_event_applied_at_utc);
        setLastSyncedAtUtc(result.snapshot.generated_at_utc);
        // The snapshot fetched because of a gap *is* the recovery, so it is not
        // evidence that nothing was missed: clearing the warning here would
        // erase it in the same breath as the refetch it describes, and a silent
        // gap is precisely what it exists to prevent. The next snapshot after
        // the recovery clears it.
        if (recoveringGapRef.current) {
          recoveringGapRef.current = false;
        } else {
          setMissedNotifications(false);
        }

        setLoaded(true);
        setError(null);
        setPhase('ready');
      } catch (cause) {
        if (cancelled || abort.signal.aborted) {
          return;
        }

        setError(toError(cause));
        setPhase((current) => (current === 'ready' ? current : 'failed'));
      } finally {
        if (!cancelled && !abort.signal.aborted) {
          setRefreshing(false);
        }
      }
    })();

    return () => {
      cancelled = true;
      abort.abort();
    };
  }, [client, config.organizationId, query, refreshToken]);

  const loadMore = useCallback(() => {
    if (nextCursor === null || loadingMore) {
      return;
    }

    setLoadingMore(true);
    void (async () => {
      try {
        const result = await client.listInbox(config.organizationId, {
          ...query,
          cursor: nextCursor,
        });
        if (result.status === 'not-modified') {
          return;
        }

        pagesRef.current += 1;
        // Cursor pages are disjoint by construction, but a concurrent commit can
        // still resend an item; keeping the first copy preserves the API order.
        setItems((current) => mergeById(current, result.snapshot.items));
        setNextCursor(result.snapshot.next_cursor);
        setLastSyncedAtUtc(result.snapshot.generated_at_utc);
        setError(null);
      } catch (cause) {
        setError(toError(cause));
      } finally {
        setLoadingMore(false);
      }
    })();
  }, [client, config.organizationId, loadingMore, nextCursor, query]);

  // Realtime channel, scoped to this principal's inbox.
  useEffect(() => {
    let stopped = false;
    const updates = createOrganizationUpdatesClient({
      baseUrl: config.apiBaseUrl,
      organizationId: config.organizationId,
      token: config.token,
      scope: 'inbox',
      handlers: {
        onStatusChanged(status) {
          if (!stopped) {
            setChannel(channelForStatus(status));
          }
        },
        onInboxChanged(notification) {
          if (stopped || !isNewerInboxNotification(sequenceRef.current, notification)) {
            return;
          }

          if (hasInboxSequenceGap(sequenceRef.current, notification)) {
            setMissedNotifications(true);
            recoveringGapRef.current = true;
          }

          sequenceRef.current = notification.sequence;
          invalidate();
        },
        onSnapshotRequired() {
          if (!stopped) {
            // A new or restored subscription recovers nothing: the sequence the
            // view knows is meaningless against the one the server resumes from.
            sequenceRef.current = null;
            refresh();
          }
        },
      },
    });

    void updates.start().catch((cause: unknown) => {
      if (!stopped) {
        setChannel('polling');
        console.warn('Realtime inbox updates unavailable; falling back to polling.', cause);
      }
    });

    return () => {
      stopped = true;
      void updates.stop().catch(() => undefined);
    };
  }, [config.apiBaseUrl, config.organizationId, config.token, invalidate, refresh]);

  // Controlled ETag polling, active exactly while the hub is not live.
  useEffect(() => {
    if (channel === 'live' || phase === 'loading') {
      return undefined;
    }

    let cancelled = false;
    const timer = setInterval(() => {
      void (async () => {
        try {
          const result = await client.listInbox(config.organizationId, query, {
            ifNoneMatch: etagRef.current,
          });
          if (cancelled) {
            return;
          }

          setError(null);
          if (result.status === 'not-modified') {
            // Nothing changed server-side, which is still an agreement.
            setLastSyncedAtUtc(new Date().toISOString());
            return;
          }

          etagRef.current = result.etag;
          if (pagesRef.current > 1) {
            setPendingUpdate(true);
            return;
          }

          setItems(result.snapshot.items);
          setNextCursor(result.snapshot.next_cursor);
          setGeneratedAtUtc(result.snapshot.generated_at_utc);
          setProjectionAppliedAtUtc(result.snapshot.last_event_applied_at_utc);
          setLastSyncedAtUtc(result.snapshot.generated_at_utc);
        } catch (cause) {
          if (!cancelled) {
            setError(toError(cause));
          }
        }
      })();
    }, config.pollIntervalMs);

    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [channel, client, config.organizationId, config.pollIntervalMs, phase, query]);

  return {
    phase,
    error,
    items,
    generatedAtUtc,
    projectionAppliedAtUtc,
    loaded,
    channel,
    lastSyncedAtUtc,
    refreshing,
    loadingMore,
    hasMore: nextCursor !== null,
    pendingUpdate,
    missedNotifications,
    loadMore,
    refresh,
  };
}

function mergeById(
  current: readonly InboxItem[],
  incoming: readonly InboxItem[],
): readonly InboxItem[] {
  const known = new Set(current.map((item) => item.item_id));
  return [...current, ...incoming.filter((item) => !known.has(item.item_id))];
}

function channelForStatus(status: OrganizationUpdatesStatus): UpdateChannel {
  switch (status) {
    case 'live':
      return 'live';
    case 'connecting':
      return 'connecting';
    case 'reconnecting':
      return 'reconnecting';
    case 'disconnected':
      return 'polling';
  }
}

function toError(cause: unknown): Error {
  return cause instanceof Error ? cause : new Error(String(cause));
}
