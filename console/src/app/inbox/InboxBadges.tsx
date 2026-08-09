import type {
  InboxApprovalState,
  InboxMessageType,
  InboxPriority,
  InboxResponseState,
} from '../../api/index.js';
import type { DeadlineView } from './inboxItemView.js';
import { messageTypeLabel, responseStateLabel } from './inboxItemView.js';

/**
 * Badges of one inbox item. Each is a label for a value the API resolved:
 * nothing here computes a state, and expiry in particular is only ever shown
 * when the projection has declared it (see `describeDeadline`).
 */

export function MessageTypeBadge({ type }: { readonly type: InboxMessageType }) {
  return (
    <span className={`inbox-badge inbox-badge--type inbox-badge--${type.toLowerCase()}`} data-type={type}>
      {messageTypeLabel(type)}
    </span>
  );
}

export function PriorityBadge({ priority }: { readonly priority: InboxPriority }) {
  return (
    <span
      className={`inbox-badge inbox-badge--priority inbox-badge--priority-${priority.toLowerCase()}`}
      data-priority={priority}
    >
      {priority}
    </span>
  );
}

export function DeadlineBadge({ deadline }: { readonly deadline: DeadlineView }) {
  if (deadline.urgency === 'none') {
    return null;
  }

  return (
    <span
      className={`inbox-badge inbox-badge--deadline inbox-badge--deadline-${deadline.urgency}`}
      data-deadline={deadline.urgency}
    >
      {deadline.label}
    </span>
  );
}

export function ResponseStateBadge({ state }: { readonly state: InboxResponseState }) {
  if (state === 'NotApplicable') {
    return null;
  }

  return (
    <span
      className={`inbox-badge inbox-badge--response inbox-badge--response-${state.toLowerCase()}`}
      data-response-state={state}
    >
      {responseStateLabel(state)}
    </span>
  );
}

export function ApprovalStateBadge({ state }: { readonly state: InboxApprovalState }) {
  return (
    <span
      className={`inbox-badge inbox-badge--approval inbox-badge--approval-${state.toLowerCase()}`}
      data-approval-state={state}
    >
      {state === 'Pending' ? 'Awaiting decision' : state}
    </span>
  );
}

export function ReminderBadge() {
  return (
    <span className="inbox-badge inbox-badge--reminder" data-reminder="Sent">
      Reminder sent
    </span>
  );
}

export function DelegatedBadge() {
  return (
    <span className="inbox-badge inbox-badge--delegated" data-delegated="true">
      Delegated
    </span>
  );
}
