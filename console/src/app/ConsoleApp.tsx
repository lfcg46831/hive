import type { ConsoleConfig } from '../config.js';
import { OrganogramView } from './organogram/OrganogramView.js';
import {
  describeLoadFailure,
  useOrganogramLiveView,
} from './organogram/useOrganogramLiveView.js';

/**
 * Shell of the read-only console. The placeholders below are deliberately
 * minimal: loading, error, empty, reconnection and fallback presentation is
 * US-F1-01-T11, and filtering is US-F1-01-T12. What this task owns is that the
 * organogram renders from the public API and never offers a way to change it.
 */
export function ConsoleApp({ config }: { readonly config: ConsoleConfig }) {
  const view = useOrganogramLiveView(config);

  return (
    <main className="console">
      <header className="console__header">
        <h1>HIVE organogram</h1>
        <p className="console__scope">Organization {config.organizationId} · read-only</p>
      </header>

      {view.phase === 'loading' ? <p className="console__status">Loading organogram…</p> : null}

      {view.phase === 'failed' && view.error !== null ? (
        <p className="console__status console__status--error" role="alert">
          {describeLoadFailure(view.error)}
        </p>
      ) : null}

      {view.snapshot === null ? null : (
        <OrganogramView
          snapshot={view.snapshot}
          liveStates={view.liveStates}
          channel={view.channel}
          lastSyncedAtUtc={view.lastSyncedAtUtc}
          registryUpdating={view.registryUpdating}
        />
      )}
    </main>
  );
}
