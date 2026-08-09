import { useEffect, useState } from 'react';
import type { InboxItem } from '../../api/index.js';
import { formatUtc } from '../format.js';
import { ApprovalStateBadge } from './InboxBadges.js';
import { InboxActionError } from './InboxActionError.js';
import type { DecisionCapability } from './inboxItemView.js';
import type { InboxActionKind, InboxEmissionOutcome } from './useInboxItemDetail.js';

export interface InboxApprovalPanelProps {
  readonly item: InboxItem;
  readonly capability: DecisionCapability;
  readonly busy: InboxActionKind | null;
  readonly actionError: Error | null;
  readonly outcome: InboxEmissionOutcome | null;
  onDecide(approved: boolean, reason: string | null): Promise<boolean>;
}

/**
 * The approval panel.
 *
 * Whether this person may decide is `approval.can_decide`, resolved by the
 * policy server-side; the panel only reflects it. Hiding the buttons is a
 * courtesy, not a control — authority is validated again when the decision is
 * emitted, and a decision this console showed as possible can still be refused.
 */
export function InboxApprovalPanel({
  item,
  capability,
  busy,
  actionError,
  outcome,
  onDecide,
}: InboxApprovalPanelProps) {
  const approval = item.approval;
  const [reason, setReason] = useState('');

  useEffect(() => {
    setReason('');
  }, [item.item_id]);

  if (approval === null) {
    return null;
  }

  const trimmedReason = reason.trim();

  return (
    <section className="inbox-panel" aria-label="Approval">
      <h4 className="inbox-panel__title">Approval</h4>

      <dl className="inbox-facts">
        <div>
          <dt>Action</dt>
          <dd>{approval.action}</dd>
        </div>
        <div>
          <dt>Policy</dt>
          <dd>
            <code>{approval.policy_ref}</code>
          </dd>
        </div>
        <div>
          <dt>Request</dt>
          <dd>
            <code>{approval.request_id}</code>
          </dd>
        </div>
        <div>
          <dt>State</dt>
          <dd>
            <ApprovalStateBadge state={approval.state} />
          </dd>
        </div>
        {approval.decided_at_utc === null ? null : (
          <div>
            <dt>Decided</dt>
            <dd>{formatUtc(approval.decided_at_utc)}</dd>
          </div>
        )}
      </dl>

      {capability.kind === 'unavailable' ? (
        <p className="inbox-panel__note">{capability.reason}</p>
      ) : (
        <form
          className="inbox-form"
          onSubmit={(event) => event.preventDefault()}
        >
          <label className="inbox-form__field">
            <span className="filters__label">Reason (optional)</span>
            <textarea
              className="inbox-form__textarea"
              value={reason}
              rows={3}
              placeholder="Recorded with the decision and visible in the audit trail."
              onChange={(event) => setReason(event.target.value)}
            />
          </label>

          <div className="inbox-form__actions">
            <button
              type="button"
              className="inbox-form__submit"
              disabled={busy !== null}
              onClick={() => {
                void onDecide(true, trimmedReason.length === 0 ? null : trimmedReason);
              }}
            >
              {busy === 'decision' ? 'Submitting…' : 'Approve'}
            </button>
            <button
              type="button"
              className="inbox-form__submit inbox-form__submit--reject"
              disabled={busy !== null}
              onClick={() => {
                void onDecide(false, trimmedReason.length === 0 ? null : trimmedReason);
              }}
            >
              {busy === 'decision' ? 'Submitting…' : 'Reject'}
            </button>
          </div>
        </form>
      )}

      {outcome?.kind === 'decision' ? (
        <p className="inbox-outcome" role="status">
          The decision was accepted: request <code>{outcome.response.request_id}</code>{' '}
          {outcome.response.approved ? 'approved' : 'rejected'} by{' '}
          {outcome.response.from_position_id}. The inbox updates once the projection applies the
          emitted ApprovalDecision.
        </p>
      ) : null}

      {actionError === null ? null : <InboxActionError error={actionError} />}
    </section>
  );
}
