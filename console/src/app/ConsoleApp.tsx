import { useState } from 'react';
import type { ConsoleConfig } from '../config.js';
import { InboxView } from './inbox/InboxView.js';
import { OrganogramView } from './organogram/OrganogramView.js';
import { useOrganogramLiveView } from './organogram/useOrganogramLiveView.js';
import { NoticeList } from './status/NoticeList.js';
import { FailurePanel, LoadingPanel } from './status/StatusPanels.js';
import { deriveConsoleStatus } from './status/consoleStatus.js';
import { useNowMs } from './status/useNowMs.js';

/**
 * Shell of the console.
 *
 * It carries two views over the same public API: the read-only organogram
 * (US-F1-01) and the human inbox (US-F1-02). They are deliberately separate
 * subtrees rather than one screen — the organogram is a view of the organization
 * and has no affordance that writes anything, while the inbox is where a person
 * acts as the position they occupy. Each owns its own transport, so a degraded
 * inbox never makes the organogram look stale, or the other way round.
 *
 * The shell owns how the organogram explains itself: loading, failure, an empty
 * organization, and the degraded modes — connecting, reconnecting, polling
 * fallback, a registry version being replaced and data that may have gone out of
 * date. The rule throughout is that a snapshot in hand is never replaced by an
 * error page: it stays on screen with the reason it stopped being refreshed
 * stated above it, because a stale organogram the reader knows about is more
 * useful than an empty screen.
 */

/** How often ages are recomputed while the view is not live. */
const FRESHNESS_TICK_MS = 1_000;

type ConsoleSection = 'organogram' | 'inbox';

const SECTIONS: readonly { readonly id: ConsoleSection; readonly label: string }[] = [
  { id: 'organogram', label: 'Organogram' },
  { id: 'inbox', label: 'Inbox' },
];

export function ConsoleApp({ config }: { readonly config: ConsoleConfig }) {
  const [section, setSection] = useState<ConsoleSection>('organogram');

  return (
    <main className="console" data-section={section}>
      <header className="console__header">
        <h1>HIVE console</h1>
        <p className="console__scope">Organization {config.organizationId}</p>
        <nav className="console__nav" aria-label="Console sections">
          {SECTIONS.map((entry) => (
            <button
              key={entry.id}
              type="button"
              className={`console__tab${section === entry.id ? ' console__tab--active' : ''}`}
              aria-current={section === entry.id ? 'page' : undefined}
              onClick={() => setSection(entry.id)}
            >
              {entry.label}
            </button>
          ))}
        </nav>
      </header>

      {/*
        Each section is mounted only while selected. The alternative — keeping
        both live — would hold two hub subscriptions and two poll loops for a
        view nobody is reading.
      */}
      {section === 'organogram' ? <OrganogramSection config={config} /> : <InboxView config={config} />}
    </main>
  );
}

function OrganogramSection({ config }: { readonly config: ConsoleConfig }) {
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
    <div className="console__section" data-stage={status.stage}>
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
    </div>
  );
}
