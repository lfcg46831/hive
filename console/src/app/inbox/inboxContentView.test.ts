import { describe, expect, it } from 'vitest';
import type { InboxMessageContent, InboxMessageType } from '../../api/index.js';
import { MESSAGE_TYPE_ORDER } from './inboxItemView.js';
import { describeContent } from './inboxContentView.js';

/**
 * The reading of the canonical content (US-F1-02-T16).
 *
 * What is asserted here is that the console names the fields of §9 and hands
 * over their text unchanged, and that the three ways a field can carry nothing —
 * absent from the message, blank in the message, or the whole content missing
 * from the projection — stay distinguishable.
 */

function fieldsOf(content: InboxMessageContent): Record<string, string | null> {
  const entries = describeContent(content).map((field) => [
    field.key,
    field.value.kind === 'text' ? field.value.text : null,
  ]);
  return Object.fromEntries(entries) as Record<string, string | null>;
}

describe('the canonical fields of each message type', () => {
  it('reads a Directive as objective and context', () => {
    expect(
      fieldsOf({ type: 'Directive', objective: 'Ship 4.2.1', context: 'Crash rate is up' }),
    ).toEqual({ objective: 'Ship 4.2.1', context: 'Crash rate is up' });
  });

  it('reads a Report as body and the kind it declares', () => {
    expect(fieldsOf({ type: 'Report', body: 'Half triaged', kind: 'progress' })).toEqual({
      body: 'Half triaged',
      kind: 'Progress',
    });
    expect(fieldsOf({ type: 'Report', body: 'All triaged', kind: 'done' })['kind']).toBe(
      'Completed',
    );
  });

  it('reads an Escalation as issue and context', () => {
    expect(
      fieldsOf({ type: 'Escalation', issue: 'No symbols', context: 'Blocked on access' }),
    ).toEqual({ issue: 'No symbols', context: 'Blocked on access' });
  });

  it('reads a Memo and a PeerResponse as a body', () => {
    expect(fieldsOf({ type: 'Memo', body: 'Notes freeze at 17:00' })).toEqual({
      body: 'Notes freeze at 17:00',
    });
    expect(fieldsOf({ type: 'PeerResponse', body: 'Symbols published' })).toEqual({
      body: 'Symbols published',
    });
  });

  it('reads a PeerRequest as an ask', () => {
    expect(fieldsOf({ type: 'PeerRequest', ask: 'Share the credentials' })).toEqual({
      ask: 'Share the credentials',
    });
  });

  it('reads an ApprovalRequest as action and justification', () => {
    expect(
      fieldsOf({ type: 'ApprovalRequest', action: 'Roll back', justification: 'Gate breached' }),
    ).toEqual({ action: 'Roll back', justification: 'Gate breached' });
  });

  it('reads an ApprovalDecision as its reason when the decision carries one', () => {
    expect(fieldsOf({ type: 'ApprovalDecision', reason: 'Too risky' })).toEqual({
      reason: 'Too risky',
    });
  });

  it('covers every message type the inbox can list', () => {
    const covered = MESSAGE_TYPE_ORDER.map((type) => describeContent(sampleContent(type)));
    expect(covered.every((fields) => fields.length > 0)).toBe(true);
  });
});

describe('a field that carries nothing', () => {
  it('separates a decision without a reason from a decision with a blank one', () => {
    const absent = describeContent({ type: 'ApprovalDecision', reason: null });
    const omitted = describeContent({ type: 'ApprovalDecision' });
    const blank = describeContent({ type: 'ApprovalDecision', reason: '   ' });

    expect(absent[0]?.value.kind).toBe('absent');
    expect(omitted[0]?.value.kind).toBe('absent');
    expect(blank[0]?.value.kind).toBe('blank');
  });

  it('reports a blank required field instead of dropping it', () => {
    const fields = describeContent({ type: 'Directive', objective: 'Ship 4.2.1', context: '' });

    expect(fields.map((field) => field.key)).toEqual(['objective', 'context']);
    expect(fields[1]?.value.kind).toBe('blank');
  });
});

describe('the text itself', () => {
  it('is handed over exactly as recorded, including markup and line breaks', () => {
    const text = '<script>alert(1)</script>\n\n  * two spaces and a newline';
    const fields = describeContent({ type: 'Memo', body: text });

    expect(fields[0]?.value).toEqual({ kind: 'text', text });
  });
});

function sampleContent(type: InboxMessageType): InboxMessageContent {
  switch (type) {
    case 'Directive':
      return { type, objective: 'o', context: 'c' };
    case 'Report':
      return { type, body: 'b', kind: 'done' };
    case 'Escalation':
      return { type, issue: 'i', context: 'c' };
    case 'Memo':
      return { type, body: 'b' };
    case 'PeerRequest':
      return { type, ask: 'a' };
    case 'PeerResponse':
      return { type, body: 'b' };
    case 'ApprovalRequest':
      return { type, action: 'a', justification: 'j' };
    case 'ApprovalDecision':
      return { type, reason: 'r' };
  }
}
