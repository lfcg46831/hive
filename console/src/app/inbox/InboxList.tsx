import type { InboxItem } from '../../api/index.js';
import { formatUtc } from '../format.js';
import {
  ApprovalStateBadge,
  DeadlineBadge,
  DelegatedBadge,
  MessageTypeBadge,
  PriorityBadge,
  ReminderBadge,
  ResponseStateBadge,
} from './InboxBadges.js';
import { describeDeadline, endpointLabel } from './inboxItemView.js';

export interface InboxListProps {
  readonly items: readonly InboxItem[];
  readonly selectedItemId: string | null;
  readonly nowMs: number;
  onSelect(itemId: string): void;
}

/**
 * The list of items assigned to the reader's positions, in the order the API
 * fixed: deadline, priority, message time, stable identifier. The console never
 * re-sorts, because a second ordering would make the cursor meaningless.
 */
export function InboxList({ items, selectedItemId, nowMs, onSelect }: InboxListProps) {
  return (
    <ul className="inbox-list" aria-label="Inbox items">
      {items.map((item) => {
        const deadline = describeDeadline(item, nowMs);
        const selected = item.item_id === selectedItemId;

        return (
          <li key={item.item_id}>
            <button
              type="button"
              className={`inbox-item${selected ? ' inbox-item--selected' : ''}${
                item.read_state === 'Unread' ? ' inbox-item--unread' : ''
              }`}
              aria-current={selected}
              data-item-id={item.item_id}
              data-read-state={item.read_state}
              onClick={() => onSelect(item.item_id)}
            >
              <span className="inbox-item__header">
                <MessageTypeBadge type={item.type} />
                <PriorityBadge priority={item.priority} />
                <DeadlineBadge deadline={deadline} />
                {item.reminder_state === 'Sent' ? <ReminderBadge /> : null}
                {item.is_delegated ? <DelegatedBadge /> : null}
              </span>

              <span className="inbox-item__from">
                From {endpointLabel(item.origin)} · to {item.assigned_position_id}
              </span>

              <span className="inbox-item__footer">
                <ResponseStateBadge state={item.response_state} />
                {item.approval === null ? null : <ApprovalStateBadge state={item.approval.state} />}
                <span className="inbox-item__timestamp">{formatUtc(item.sent_at_utc)}</span>
              </span>
            </button>
          </li>
        );
      })}
    </ul>
  );
}
