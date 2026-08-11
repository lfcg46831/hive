import { formatUtc } from '../format.js';
import {
  DeadlineBadge,
  DelegatedBadge,
  MessageTypeBadge,
  PriorityBadge,
  ReminderBadge,
  ResponseStateBadge,
} from './InboxBadges.js';
import { InboxApprovalPanel } from './InboxApprovalPanel.js';
import { InboxMessageContentPanel } from './InboxMessageContentPanel.js';
import { InboxReplyForm } from './InboxReplyForm.js';
import { describeDeadline, describeDecision, describeReply, endpointLabel } from './inboxItemView.js';
import type { InboxItemDetailView } from './useInboxItemDetail.js';

export interface InboxItemDetailProps {
  readonly detail: InboxItemDetailView;
  readonly nowMs: number;
}

/**
 * The selected item: its correlation, its deadline, the message itself, and the
 * two things a person can do with it.
 *
 * The order is the point. Metadata answers «what is this and by when»; the
 * content answers «what is being asked»; the forms answer it. Anchoring both
 * forms below the content means the response is always composed with the message
 * in view, which is what makes a human occupant's answer equivalent to an AI
 * occupant's — the same fields, read before answering.
 */
export function InboxItemDetail({ detail, nowMs }: InboxItemDetailProps) {
  if (detail.phase === 'idle') {
    return (
      <section className="inbox-detail inbox-detail--empty" aria-label="Inbox item">
        <p className="panel__detail">Select an item to see its correlation, deadline and actions.</p>
      </section>
    );
  }

  if (detail.phase === 'failed') {
    return (
      <section className="inbox-detail" aria-label="Inbox item">
        <div className="panel panel--error" role="alert">
          <p className="panel__title">This item could not be loaded</p>
          <p className="panel__detail">{detail.error?.message ?? 'The API did not answer.'}</p>
          <button type="button" className="panel__action" onClick={detail.reload}>
            Try again
          </button>
        </div>
      </section>
    );
  }

  const item = detail.item;
  if (item === null) {
    return (
      <section className="inbox-detail" aria-label="Inbox item" aria-busy="true">
        <p className="panel__detail">Loading the item…</p>
      </section>
    );
  }

  const deadline = describeDeadline(item, nowMs);

  return (
    <section className="inbox-detail" aria-label="Inbox item" data-item-id={item.item_id}>
      <header className="inbox-detail__header">
        <div className="inbox-item__header">
          <MessageTypeBadge type={item.type} />
          <PriorityBadge priority={item.priority} />
          <DeadlineBadge deadline={deadline} />
          <ResponseStateBadge state={item.response_state} />
          {item.reminder_state === 'Sent' ? <ReminderBadge /> : null}
          {item.is_delegated ? <DelegatedBadge /> : null}
        </div>

        <button
          type="button"
          className="filters__clear"
          disabled={detail.busy !== null}
          onClick={() => detail.setRead(item.read_state === 'Unread')}
        >
          {item.read_state === 'Unread' ? 'Mark as read' : 'Mark as unread'}
        </button>
      </header>

      <dl className="inbox-facts">
        <div>
          <dt>From</dt>
          <dd>{endpointLabel(item.origin)}</dd>
        </div>
        <div>
          <dt>Assigned position</dt>
          <dd>{item.assigned_position_id}</dd>
        </div>
        <div>
          <dt>Thread</dt>
          <dd>
            <code>{item.thread_id}</code>
          </dd>
        </div>
        <div>
          <dt>Message</dt>
          <dd>
            <code>{item.message_id}</code>
          </dd>
        </div>
        <div>
          <dt>Sent</dt>
          <dd>{formatUtc(item.sent_at_utc)}</dd>
        </div>
        <div>
          <dt>Deadline</dt>
          <dd>
            {item.deadline_at_utc === null ? 'None' : formatUtc(item.deadline_at_utc)}
            {item.is_expired ? ' · expired' : ''}
          </dd>
        </div>
        {item.last_reminder_at_utc === null ? null : (
          <div>
            <dt>Reminder</dt>
            <dd>{formatUtc(item.last_reminder_at_utc)}</dd>
          </div>
        )}
      </dl>

      <InboxMessageContentPanel content={detail.content} type={item.type} />

      {item.type === 'ApprovalRequest' ? (
        <InboxApprovalPanel
          item={item}
          capability={describeDecision(item)}
          busy={detail.busy}
          actionError={detail.actionError}
          outcome={detail.outcome}
          onDecide={detail.decide}
        />
      ) : null}

      <InboxReplyForm
        item={item}
        capability={describeReply(item)}
        draftText={detail.draftText}
        busy={detail.busy}
        actionError={detail.actionError}
        outcome={detail.outcome}
        onSaveDraft={detail.saveDraft}
        onReply={detail.reply}
      />
    </section>
  );
}
