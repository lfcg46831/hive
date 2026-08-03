import type { PositionOperationalState } from '../../api/index.js';

/**
 * The API resolves operational-state precedence; the console only labels what
 * it is given and never re-derives a state from underlying events.
 */
const STATE_LABELS: Readonly<Record<PositionOperationalState, string>> = {
  Offline: 'Offline',
  Blocked: 'Blocked',
  WaitingHuman: 'Waiting for human',
  Working: 'Working',
  Idle: 'Idle',
};

export function PositionStateBadge({ state }: { readonly state: PositionOperationalState }) {
  return (
    <span className={`state-badge state-badge--${state.toLowerCase()}`} data-state={state}>
      {STATE_LABELS[state]}
    </span>
  );
}
