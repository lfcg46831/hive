import type { RegistryVersion } from '../../api/index.js';
import { formatUtc } from '../format.js';
import type { ConsoleFreshness } from '../status/consoleStatus.js';
import type { UpdateChannel } from './useOrganogramLiveView.js';

export interface UpdateIndicatorProps {
  readonly channel: UpdateChannel;
  readonly freshness: ConsoleFreshness;
  readonly lastSyncedAtUtc: string | null;
  /** Last event applied by the server projection, when a poll has reported it. */
  readonly projectionAppliedAtUtc: string | null;
  readonly registry: RegistryVersion | null;
  readonly registryUpdating: boolean;
  readonly refreshing: boolean;
}

const CHANNEL_LABELS: Readonly<Record<UpdateChannel, string>> = {
  connecting: 'Connecting to live updates',
  live: 'Live',
  reconnecting: 'Reconnecting — polling meanwhile',
  polling: 'Polling fallback',
};

/**
 * Says how the view is being kept current. Realtime is an optimization, so the
 * degraded modes are named explicitly: a reader must be able to tell a live
 * organogram from one that is a poll interval behind, and both from one whose
 * updates have stopped arriving altogether.
 */
export function UpdateIndicator({
  channel,
  freshness,
  lastSyncedAtUtc,
  projectionAppliedAtUtc,
  registry,
  registryUpdating,
  refreshing,
}: UpdateIndicatorProps) {
  return (
    <div
      className="update-indicator"
      data-channel={channel}
      data-freshness={freshness.level}
      role="status"
      aria-live="polite"
    >
      <span className={`update-indicator__dot update-indicator__dot--${channel}`} aria-hidden="true" />
      <span className="update-indicator__channel">{CHANNEL_LABELS[channel]}</span>
      <span
        className={`update-indicator__freshness update-indicator__freshness--${freshness.level}`}
        title={describeSync(lastSyncedAtUtc, projectionAppliedAtUtc)}
      >
        {freshness.label}
      </span>
      {registry === null ? null : (
        <span className="update-indicator__registry" title={registry.fingerprint}>
          Registry v{registry.version}
          {registryUpdating ? ' · updating' : ''}
        </span>
      )}
      {refreshing ? <span className="update-indicator__refreshing">Refreshing…</span> : null}
    </div>
  );
}

/**
 * The two timestamps that make a freshness claim checkable: when the client last
 * agreed with the API, and how far the server-side projection had itself got.
 * The projection signal only exists once `/position-states` has been read, and
 * is omitted rather than guessed until then.
 */
function describeSync(lastSyncedAtUtc: string | null, projectionAppliedAtUtc: string | null): string {
  const synced = `Last agreement with the API: ${formatUtc(lastSyncedAtUtc)}`;
  return projectionAppliedAtUtc === null
    ? synced
    : `${synced} · last event applied by the projection: ${formatUtc(projectionAppliedAtUtc)}`;
}
