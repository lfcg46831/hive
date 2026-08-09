import { useEffect, useState } from 'react';
import type { InboxItem } from '../../api/index.js';
import { InboxActionError } from './InboxActionError.js';
import { messageTypeLabel } from './inboxItemView.js';
import type { ReplyCapability } from './inboxItemView.js';
import type { InboxActionKind, InboxEmissionOutcome } from './useInboxItemDetail.js';

export interface InboxReplyFormProps {
  readonly item: InboxItem;
  readonly capability: ReplyCapability;
  readonly draftText: string | null;
  readonly busy: InboxActionKind | null;
  readonly actionError: Error | null;
  readonly outcome: InboxEmissionOutcome | null;
  onSaveDraft(body: string | null): void;
  onReply(body: string, reportKind: string | null): Promise<boolean>;
}

/** Report kinds the reply contract accepts for a Directive. */
const REPORT_KINDS = [
  { value: 'progress', label: 'Progress' },
  { value: 'done', label: 'Completed' },
] as const;

/**
 * The human response form.
 *
 * Sending is not a message: it is a request to the position this person occupies
 * to emit one. The form says so, and says which canonical message will be
 * emitted, because a person answering as a position should know what the
 * organization will record. The draft is server-side state — it survives a
 * reload and a passivated actor — so it is saved explicitly rather than kept in
 * the browser.
 */
export function InboxReplyForm({
  item,
  capability,
  draftText,
  busy,
  actionError,
  outcome,
  onSaveDraft,
  onReply,
}: InboxReplyFormProps) {
  const [body, setBody] = useState(draftText ?? '');
  const [reportKind, setReportKind] = useState<string>(REPORT_KINDS[0].value);

  // The persisted draft wins whenever the server reports a different one, which
  // includes selecting another item and reloading after an emission.
  useEffect(() => {
    setBody(draftText ?? '');
  }, [draftText, item.item_id]);

  if (capability.kind === 'unavailable') {
    return (
      <section className="inbox-panel" aria-label="Response">
        <h4 className="inbox-panel__title">Response</h4>
        <p className="inbox-panel__note">{capability.reason}</p>
      </section>
    );
  }

  const trimmed = body.trim();
  const canSend = trimmed.length > 0 && busy === null;

  return (
    <section className="inbox-panel" aria-label="Response">
      <h4 className="inbox-panel__title">Response</h4>
      <p className="inbox-panel__note">
        Sending asks <strong>{item.assigned_position_id}</strong> to emit a{' '}
        <strong>{messageTypeLabel(capability.emits)}</strong> in this thread. The message is
        validated and audited by the organization, not composed here.
      </p>

      <form
        className="inbox-form"
        onSubmit={(event) => {
          event.preventDefault();
          void (async () => {
            const sent = await onReply(
              trimmed,
              capability.requiresReportKind ? reportKind : null,
            );
            if (sent) {
              setBody('');
            }
          })();
        }}
      >
        {capability.requiresReportKind ? (
          <fieldset className="inbox-form__fieldset">
            <legend className="filters__label">Report kind</legend>
            {REPORT_KINDS.map((kind) => (
              <label key={kind.value} className="inbox-form__radio">
                <input
                  type="radio"
                  name="report-kind"
                  value={kind.value}
                  checked={reportKind === kind.value}
                  onChange={() => setReportKind(kind.value)}
                />
                <span>{kind.label}</span>
              </label>
            ))}
          </fieldset>
        ) : null}

        <label className="inbox-form__field">
          <span className="filters__label">Message</span>
          <textarea
            className="inbox-form__textarea"
            value={body}
            rows={5}
            placeholder="Plain text. Attachments and rich text are not part of this surface."
            onChange={(event) => setBody(event.target.value)}
          />
        </label>

        <div className="inbox-form__actions">
          <button type="submit" className="inbox-form__submit" disabled={!canSend}>
            {busy === 'reply' ? 'Sending…' : 'Send response'}
          </button>
          <button
            type="button"
            className="filters__clear"
            disabled={busy !== null || trimmed.length === 0}
            onClick={() => onSaveDraft(body)}
          >
            {busy === 'draft' ? 'Saving…' : 'Save draft'}
          </button>
          <button
            type="button"
            className="filters__clear"
            disabled={busy !== null || draftText === null}
            onClick={() => onSaveDraft('')}
          >
            Discard draft
          </button>
        </div>
      </form>

      {capability.inProgress ? (
        <p className="inbox-panel__note">
          A response is in progress for this item. That is your own interface state; nothing has
          been emitted yet.
        </p>
      ) : null}

      {outcome?.kind === 'reply' ? (
        <p className="inbox-outcome" role="status">
          {messageTypeLabel(outcome.response.type)} <code>{outcome.response.message_id}</code> was
          accepted for emission by {outcome.response.from_position_id} to{' '}
          {outcome.response.to_position_id}. The inbox updates once the projection applies it.
        </p>
      ) : null}

      {actionError === null ? null : <InboxActionError error={actionError} />}
    </section>
  );
}
