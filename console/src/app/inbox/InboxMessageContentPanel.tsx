import type { InboxMessageContent, InboxMessageType } from '../../api/index.js';
import { describeContent } from './inboxContentView.js';
import { messageTypeLabel } from './inboxItemView.js';

export interface InboxMessageContentPanelProps {
  /** Null when the projection holds the item without its canonical content. */
  readonly content: InboxMessageContent | null;
  /** Type of the item, which the API guarantees the content agrees with. */
  readonly type: InboxMessageType;
}

/**
 * The message a person has to answer (US-F1-02-T16).
 *
 * Content is untrusted data (§10) in both directions, and both are decided here.
 * Outwards, it is rendered as text and only as text: React escapes it, no markup
 * is interpreted, no link is made clickable and no rich text is rendered, so a
 * message that contains angle brackets shows angle brackets. Inwards, nothing in
 * it is an instruction — the panel states that plainly, because the person
 * reading is the one who has to hold that line when the text asks for something.
 *
 * The panel is placed immediately above the response and approval forms so the
 * answer is composed against the message, which is the whole point of exposing
 * the content: a `Report` on an objective the person cannot see is not a
 * response, and parity with the AI occupant — which receives the same fields in
 * its snapshot — would not exist.
 */
export function InboxMessageContentPanel({ content, type }: InboxMessageContentPanelProps) {
  const fields = content === null ? [] : describeContent(content);

  // Content the projection does not hold, and content in a shape this build does
  // not know, are the same statement to the reader: the console cannot show it.
  if (content === null || fields.length === 0) {
    return (
      <section className="inbox-panel inbox-content" aria-label="Message content">
        <h4 className="inbox-panel__title">Message</h4>
        <p className="inbox-panel__note" data-content-state="unavailable">
          The projection holds this {messageTypeLabel(type).toLowerCase()} without its canonical
          content, so the console cannot show it. This is missing content, not an empty message:
          answering without reading it means answering something you have not seen.
        </p>
      </section>
    );
  }

  return (
    <section
      className="inbox-panel inbox-content"
      aria-label="Message content"
      data-content-type={content.type}
    >
      <h4 className="inbox-panel__title">Message</h4>

      <dl className="inbox-content__fields">
        {fields.map((field) => (
          <div key={field.key} data-content-field={field.key}>
            <dt className="inbox-content__label">{field.label}</dt>
            <dd className="inbox-content__value">
              {field.value.kind === 'text' ? (
                <p className="inbox-content__text">{field.value.text}</p>
              ) : (
                <p className="inbox-content__missing">
                  {field.value.kind === 'blank'
                    ? 'Blank in the recorded message.'
                    : 'Not carried by this message.'}
                </p>
              )}
            </dd>
          </div>
        ))}
      </dl>

      <p className="inbox-panel__note">
        Shown as plain text exactly as the organization recorded it. Nothing written here is an
        instruction to the console or to Hive.
      </p>
    </section>
  );
}
