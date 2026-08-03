import type { ConsoleConfig } from '../config.js';
import { OrganogramView } from './organogram/OrganogramView.js';
import { useOrganogramLiveView } from './organogram/useOrganogramLiveView.js';
import { NoticeList } from './status/NoticeList.js';
import { FailurePanel, LoadingPanel } from './status/StatusPanels.js';
import { deriveConsoleStatus } from './status/consoleStatus.js';
import { useNowMs } from './status/useNowMs.js';

/**
 * Shell of the read-only console.
 *
 * The shell owns how the console explains itself: loading, failure, an empty
 * organization, and the degraded modes — connecting, reconnecting, polling
 * fallback, a registry version being replaced and data that may have gone out of
 * date. The rule throughout is that a snapshot in hand is never replaced by an
 * error page: it stays on screen with the reason it stopped being refreshed
 * stated above it, because a stale organogram the reader knows about is more
 * useful than an empty screen. The only interactive affordances are re-reads.
 */

/** How often ages are recomputed while the view is not live. */
const FRESHNESS_TICK_MS = 1_000;

export function ConsoleApp({ config }: { readonly config: ConsoleConfig }) {
  const view = useOrganogramLiveView(config);
  // A live subscription displays no age, so the clock only runs when degraded.
  const nowMs = useNowMs(FRESHNESS_TICK_MS, view.channel !== 'live');

  const status = deriveConsoleStatus({
    phase: view.phase,
    error: view.error,
    snapshot: view.snapshot,
    channel: view.channel,
    lastSyncedAtUtc: view.lastSyncedAtUtc,
    registryUpdating: view.registryUpdating,
    refreshing: view.refreshing,
    pollIntervalMs: config.pollIntervalMs,
    nowMs,
  });

  return (
    <main className="console" data-stage={status.stage}>
      <header className="console__header">
        <h1>HIVE organogram</h1>
        <p className="console__scope">Organization {config.organizationId} · read-only</p>
      </header>

      <NoticeList notices={status.notices} onRetry={view.refresh} />

      {status.stage === 'loading' ? <LoadingPanel /> : null}

      {status.stage === 'failed' && status.failure !== null ? (
        <FailurePanel failure={status.failure} onRetry={view.refresh} />
      ) : null}

      {view.snapshot === null ? null : (
        <OrganogramView
          snapshot={view.snapshot}
          liveStates={view.liveStates}
          channel={view.channel}
          freshness={status.freshness}
          lastSyncedAtUtc={view.lastSyncedAtUtc}
          projectionAppliedAtUtc={view.projectionAppliedAtUtc}
          registryUpdating={view.registryUpdating}
          refreshing={status.refreshing}
        />
      )}
    </main>
  );
}
