/**
 * Presentation rules of the canonical message content (US-F1-02-T16).
 *
 * The reading is deliberately dumb. The public contract is closed and typed per
 * message type, so this module only names the fields of §9 and hands their text
 * over untouched: no parsing, no truncation, no interpretation of what the text
 * says. Three distinctions are load-bearing and must not collapse into one:
 *
 * - Content the projection does not hold (`content: null`) is unknown content,
 *   not an empty message. The view says the console cannot show it rather than
 *   presenting an empty body as the message the organization recorded.
 * - A field whose canonical value is absent (`ApprovalDecision.reason`) is a
 *   message that carries no reason, which is a fact about the decision.
 * - A field that is present but blank is blank text — reported as such, because
 *   silently hiding it would claim the field does not exist.
 */

import type { InboxMessageContent, InboxReportKind } from '../../api/index.js';

export type ContentFieldValue =
  /** Text exactly as the organization recorded it; never markup. */
  | { readonly kind: 'text'; readonly text: string }
  /** Present in the contract, blank in this message. */
  | { readonly kind: 'blank' }
  /** Not carried by this canonical message at all. */
  | { readonly kind: 'absent' };

export interface ContentField {
  /** Stable key of the wire field, used by the view and by tests. */
  readonly key: string;
  readonly label: string;
  readonly value: ContentFieldValue;
}

const REPORT_KIND_LABELS: Readonly<Record<InboxReportKind, string>> = {
  progress: 'Progress',
  done: 'Completed',
};

/**
 * The fields of one canonical message, in the order §9 declares them. The switch
 * is exhaustive over the closed union, so a new message type fails to compile
 * here instead of silently rendering nothing.
 */
export function describeContent(content: InboxMessageContent): readonly ContentField[] {
  switch (content.type) {
    case 'Directive':
      return [
        textField('objective', 'Objective', content.objective),
        textField('context', 'Context', content.context),
      ];
    case 'Report':
      return [
        textField('body', 'Body', content.body),
        {
          key: 'kind',
          label: 'Report kind',
          value: { kind: 'text', text: REPORT_KIND_LABELS[content.kind] },
        },
      ];
    case 'Escalation':
      return [
        textField('issue', 'Issue', content.issue),
        textField('context', 'Context', content.context),
      ];
    case 'Memo':
      return [textField('body', 'Body', content.body)];
    case 'PeerRequest':
      return [textField('ask', 'Ask', content.ask)];
    case 'PeerResponse':
      return [textField('body', 'Body', content.body)];
    case 'ApprovalRequest':
      return [
        textField('action', 'Action', content.action),
        textField('justification', 'Justification', content.justification),
      ];
    case 'ApprovalDecision':
      return [
        {
          key: 'reason',
          label: 'Reason',
          value:
            content.reason === null || content.reason === undefined
              ? { kind: 'absent' }
              : blankOrText(content.reason),
        },
      ];
    default:
      // Unreachable for the published union. A content shape this build does not
      // know is reported as unshowable rather than rendered as an empty message.
      return [];
  }
}

function textField(key: string, label: string, text: string): ContentField {
  return { key, label, value: blankOrText(text) };
}

function blankOrText(text: string): ContentFieldValue {
  return text.trim().length === 0 ? { kind: 'blank' } : { kind: 'text', text };
}
