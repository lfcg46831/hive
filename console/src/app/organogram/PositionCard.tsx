import type { OrganizationPosition, OrganizationPositionState } from '../../api/index.js';
import { formatUtc } from '../format.js';
import { PositionStateBadge } from './PositionStateBadge.js';

export interface PositionCardProps {
  readonly position: OrganizationPosition;
  readonly state: OrganizationPositionState;
  readonly isLeadership: boolean;
}

/**
 * A position as the organogram shows it: who holds it, what it is doing and
 * what the projection last saw. Strictly read-only — structural editing lands
 * in F2 and no affordance for it may exist here.
 */
export function PositionCard({ position, state, isLeadership }: PositionCardProps) {
  const event = state.last_correlated_event;
  const subordinates = position.hierarchy.direct_subordinate_position_ids.length;

  return (
    <li className={`position${isLeadership ? ' position--leadership' : ''}`} data-position-id={position.id}>
      <div className="position__header">
        <span className="position__name">{position.name ?? position.id}</span>
        {isLeadership ? <span className="position__tag">Unit leadership</span> : null}
        <PositionStateBadge state={state.state} />
      </div>

      <dl className="position__facts">
        <div>
          <dt>Occupant</dt>
          <dd>
            {position.occupant.id ?? 'Vacant'}
            <span className="position__occupant-type">
              {position.occupant.type === 'Human' ? 'human' : 'AI agent'}
            </span>
          </dd>
        </div>
        <div>
          <dt>Reports to</dt>
          <dd>{position.hierarchy.reports_to_position_id ?? '—'}</dd>
        </div>
        <div>
          <dt>Direct subordinates</dt>
          <dd>{subordinates}</dd>
        </div>
        <div>
          <dt>Last correlated event</dt>
          <dd>
            {event === null ? (
              '—'
            ) : (
              <>
                {event.type}
                <span className="position__thread">thread {event.thread_id}</span>
                <span className="position__timestamp">{formatUtc(event.occurred_at_utc)}</span>
              </>
            )}
          </dd>
        </div>
      </dl>

      <p className="position__footer">
        State updated {formatUtc(state.updated_at_utc)} · sequence {state.sequence}
      </p>
    </li>
  );
}
