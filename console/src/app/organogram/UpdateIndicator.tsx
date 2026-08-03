import type { RegistryVersion } from '../../api/index.js';
import { formatUtc } from '../format.js';
import type { UpdateChannel } from './useOrganogramLiveView.js';

export interface UpdateIndicatorProps {
  readonly channel: UpdateChannel;
  readonly lastSyncedAtUtc: string | null;
  readonly registry: RegistryVersion | null;
  readonly registryUpdating: boolean;
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
 * organogram from one that is a poll interval behind.
 */
export function UpdateIndicator({
  channel,
  lastSyncedAtUtc,
  registry,
  registryUpdating,
}: UpdateIndicatorProps) {
  return (
    <div className="update-indicator" data-channel={channel} role="status" aria-live="polite">
      <span className={`update-indicator__dot update-indicator__dot--${channel}`} aria-hidden="true" />
      <span className="update-indicator__channel">{CHANNEL_LABELS[channel]}</span>
      <span className="update-indicator__synced">Last update {formatUtc(lastSyncedAtUtc)}</span>
      {registry === null ? null : (
        <span className="update-indicator__registry" title={registry.fingerprint}>
          Registry v{registry.version}
          {registryUpdating ? ' · updating' : ''}
        </span>
      )}
    </div>
  );
}
