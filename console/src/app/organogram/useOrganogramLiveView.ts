/**
 * Data layer of the read-only organogram view.
 *
 * The REST snapshot is the only source of truth. The SignalR hub is an
 * optimization on top of it: a live notification may advance a position state,
 * but every connection, reconnection or sequence gap sends the view back to
 * `/organogram` and `/position-states`. When the hub is not live the same data
 * arrives through controlled ETag polling, and the view says so — a console
 * that silently shows stale states is worse than one that admits it is behind.
 *
 * This hook holds only facts: which channel is carrying updates, when the view
 * last agreed with the server, whether a refetch is in flight. Judging those
 * facts into what the reader is told belongs to `app/status/consoleStatus.ts`.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type {
  OrganizationPositionState,
  OrganizationUpdatesStatus,
  OrganogramResponse,
  RegistryVersion,
} from '../../api/index.js';
import {
  createHiveApiClient,
  createOrganizationUpdatesClient,
  isNewerPositionState,
} from '../../api/index.js';
import type { ConsoleConfig } from '../../config.js';

export type LiveViewPhase = 'loading' | 'ready' | 'failed';

/** How the view is currently being kept up to date. */
export type UpdateChannel = 'connecting' | 'live' | 'reconnecting' | 'polling';

export interface OrganogramLiveView {
  readonly phase: LiveViewPhase;
  readonly error: Error | null;
  readonly snapshot: OrganogramResponse | null;
  /** Live overrides of `position.operational_state`, keyed by position id. */
  readonly liveStates: ReadonlyMap<string, OrganizationPositionState>;
  readonly channel: UpdateChannel;
  /** When the view last agreed with the server, live or polled. */
  readonly lastSyncedAtUtc: string | null;
  /**
   * Timestamp of the last event applied by the server-side projection, as
   * reported by `/position-states`. Null until the first poll: the organogram
   * response does not carry the signal, and the console does not invent it.
   */
  readonly projectionAppliedAtUtc: string | null;
  /** True between an `OrganogramChanged` notification and the refetch. */
  readonly registryUpdating: boolean;
  /** True while a snapshot refetch is in flight over an already shown view. */
  readonly refreshing: boolean;
  readonly registry: RegistryVersion | null;
  refresh(): void;
}

export function useOrganogramLiveView(config: ConsoleConfig): OrganogramLiveView {
  const client = useMemo(
    () => createHiveApiClient({ baseUrl: config.apiBaseUrl, token: config.token }),
    [config.apiBaseUrl, config.token],
  );

  const [phase, setPhase] = useState<LiveViewPhase>('loading');
  const [error, setError] = useState<Error | null>(null);
  const [snapshot, setSnapshot] = useState<OrganogramResponse | null>(null);
  const [liveStates, setLiveStates] = useState<ReadonlyMap<string, OrganizationPositionState>>(
    new Map(),
  );
  const [channel, setChannel] = useState<UpdateChannel>('connecting');
  const [lastSyncedAtUtc, setLastSyncedAtUtc] = useState<string | null>(null);
  const [projectionAppliedAtUtc, setProjectionAppliedAtUtc] = useState<string | null>(null);
  const [registryUpdating, setRegistryUpdating] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [refreshToken, setRefreshToken] = useState(0);

  const etagRef = useRef<string | null>(null);
  const statesRef = useRef<ReadonlyMap<string, OrganizationPositionState>>(new Map());

  const applyStates = useCallback((incoming: readonly OrganizationPositionState[]): void => {
    const next = new Map(statesRef.current);
    let changed = false;
    for (const state of incoming) {
      if (isNewerPositionState(next.get(state.position_id), state)) {
        next.set(state.position_id, state);
        changed = true;
      }
    }

    if (changed) {
      statesRef.current = next;
      setLiveStates(next);
    }
  }, []);

  const refresh = useCallback(() => setRefreshToken((token) => token + 1), []);

  // Authoritative snapshot. Re-runs on explicit refresh and on every hub event
  // that invalidates what the view holds.
  useEffect(() => {
    const abort = new AbortController();
    let cancelled = false;
    setRefreshing(true);

    void (async () => {
      try {
        const response = await client.getOrganogram(config.organizationId, {
          signal: abort.signal,
        });
        if (cancelled) {
          return;
        }

        // The snapshot carries its own states; live overrides start from it so
        // a stale override can never survive a refetch.
        statesRef.current = new Map(
          response.positions.map((position) => [position.id, position.operational_state]),
        );
        etagRef.current = null;
        setSnapshot(response);
        setLiveStates(statesRef.current);
        setLastSyncedAtUtc(response.generated_at_utc);
        setRegistryUpdating(false);
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
  }, [client, config.organizationId, refreshToken]);

  // Realtime channel. Notifications only ever move a position forward by
  // sequence; anything structural sends the view back to REST.
  useEffect(() => {
    let stopped = false;
    const updates = createOrganizationUpdatesClient({
      baseUrl: config.apiBaseUrl,
      organizationId: config.organizationId,
      token: config.token,
      handlers: {
        onStatusChanged(status) {
          if (!stopped) {
            setChannel(channelForStatus(status));
          }
        },
        onOrganogramChanged() {
          if (!stopped) {
            setRegistryUpdating(true);
            refresh();
          }
        },
        onPositionStateChanged(notification) {
          if (!stopped) {
            applyStates([notification.state]);
            setLastSyncedAtUtc(notification.state.updated_at_utc);
          }
        },
        onSnapshotRequired() {
          if (!stopped) {
            refresh();
          }
        },
      },
    });

    void updates.start().catch((cause: unknown) => {
      if (!stopped) {
        // A hub that will not start is not a view failure: polling takes over.
        setChannel('polling');
        console.warn('Realtime updates unavailable; falling back to polling.', cause);
      }
    });

    return () => {
      stopped = true;
      void updates.stop().catch(() => undefined);
    };
  }, [applyStates, config.apiBaseUrl, config.organizationId, config.token, refresh]);

  // Controlled polling fallback, active exactly while the hub is not live.
  useEffect(() => {
    if (channel === 'live' || phase === 'loading') {
      return undefined;
    }

    let cancelled = false;
    const timer = setInterval(() => {
      void (async () => {
        try {
          const result = await client.getPositionStates(config.organizationId, {
            ifNoneMatch: etagRef.current,
          });
          if (cancelled) {
            return;
          }

          etagRef.current = result.etag;
          if (result.status === 'modified') {
            applyStates(result.snapshot.states);
            setLastSyncedAtUtc(result.snapshot.generated_at_utc);
            setProjectionAppliedAtUtc(result.snapshot.last_event_applied_at_utc);
            setError(null);
          } else {
            // Nothing changed server-side, which is still an agreement: the view
            // is current as of now, not as of the last state transition.
            setLastSyncedAtUtc(new Date().toISOString());
            setError(null);
          }
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
  }, [applyStates, channel, client, config.organizationId, config.pollIntervalMs, phase]);

  return {
    phase,
    error,
    snapshot,
    liveStates,
    channel,
    lastSyncedAtUtc,
    projectionAppliedAtUtc,
    registryUpdating,
    refreshing,
    registry: snapshot?.registry ?? null,
    refresh,
  };
}

/**
 * A hub that is connecting, reconnecting or closed leaves the view on the
 * polling fallback; only an established subscription counts as live.
 */
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
