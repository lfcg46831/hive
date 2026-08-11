// @vitest-environment jsdom

/**
 * The canonical message content in the console detail (US-F1-02-T16).
 *
 * The properties under test are the ones that make the content safe to show and
 * worth showing:
 *
 * - Every message type of §9 renders its own typed fields, so a person answering
 *   as a position reads the same fields the AI occupant receives.
 * - The content is text and only text. Markup is escaped, never interpreted, and
 *   nothing in it is presented as an instruction to the console.
 * - The response and approval forms sit below the content, so an answer is
 *   always composed with the message in view.
 * - Absent content is said out loud. A projection that holds an item without its
 *   content is not an empty message, and the console must not let the two look
 *   the same.
 * - Content stays a property of the detail route: the list never carries it, and
 *   selecting another item never leaves the previous message on screen.
 */

import { act, cleanup, render, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { InboxMessageType } from '../../api/index.js';
import type { ConsoleConfig } from '../../config.js';
import {
  INBOX_NOW_UTC,
  INBOX_ORGANIZATION_ID,
  approvalRequest,
  createInboxServer,
  inboxItem,
  messageContent,
} from '../testing/inboxFixture.js';
import type { InboxServer } from '../testing/inboxFixture.js';
import { InboxView } from './InboxView.js';

vi.mock('@microsoft/signalr', async () => {
  const fake = await import('../testing/signalrFake.js');
  return { HubConnectionBuilder: fake.FakeHubConnectionBuilder, LogLevel: fake.LogLevel };
});

const CONFIG: ConsoleConfig = {
  apiBaseUrl: 'https://hive.example.com',
  organizationId: INBOX_ORGANIZATION_ID,
  token: 'person-token',
  pollIntervalMs: 3_600_000,
};

let server: InboxServer;
let warn: ReturnType<typeof vi.spyOn>;

beforeEach(async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.setSystemTime(new Date(INBOX_NOW_UTC));
  warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  const { hubControl } = await import('../testing/signalrFake.js');
  hubControl.reset();

  server = createInboxServer();
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string, init?: RequestInit) => server.fetch(input, init)),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  warn.mockRestore();
  vi.useRealTimers();
});

/** Renders the inbox and waits until the given item is the one on the detail. */
async function renderShowing(itemId: string): Promise<void> {
  render(<InboxView config={CONFIG} />);
  await waitFor(() =>
    expect(document.querySelector('.inbox-detail')?.getAttribute('data-item-id')).toBe(itemId),
  );
}

function contentPanel(): HTMLElement {
  const panel = document.querySelector<HTMLElement>('.inbox-content');
  if (panel === null) {
    throw new Error('The detail shows no message content panel.');
  }

  return panel;
}

/** Text of one canonical field, or null when the field renders no text. */
function fieldText(key: string): string | null {
  const row = contentPanel().querySelector(`[data-content-field="${key}"]`);
  if (row === null) {
    throw new Error(`The content panel has no ${key} field.`);
  }

  return row.querySelector('.inbox-content__text')?.textContent ?? null;
}

function fieldNote(key: string): string | null {
  return (
    contentPanel()
      .querySelector(`[data-content-field="${key}"] .inbox-content__missing`)
      ?.textContent ?? null
  );
}

function fieldKeys(): string[] {
  return [...contentPanel().querySelectorAll('[data-content-field]')].map(
    (row) => row.getAttribute('data-content-field') ?? '',
  );
}

/** Index of an element in the detail's DOM order, for anchoring assertions. */
function positionOf(selector: string): number {
  const detail = document.querySelector('.inbox-detail');
  if (detail === null) {
    throw new Error('No detail is on screen.');
  }

  const nodes = [...detail.querySelectorAll('section, .inbox-content')];
  return nodes.findIndex((node) => node.matches(selector));
}

/** Puts one item of the given type in the inbox and shows it. */
async function showItemOfType(type: InboxMessageType): Promise<void> {
  server.items = [inboxItem({ id: 'item-1', type })];
  await renderShowing('item-1');
}

describe('every message type shows its canonical fields', () => {
  it('shows objective and context for a Directive', async () => {
    await showItemOfType('Directive');
    const content = messageContent('Directive');

    expect(contentPanel().getAttribute('data-content-type')).toBe('Directive');
    expect(fieldKeys()).toEqual(['objective', 'context']);
    expect(fieldText('objective')).toBe(
      content.type === 'Directive' ? content.objective : undefined,
    );
    expect(fieldText('context')).toBe(content.type === 'Directive' ? content.context : undefined);
  });

  it('shows body and the declared kind for a Report', async () => {
    await showItemOfType('Report');

    expect(fieldKeys()).toEqual(['body', 'kind']);
    expect(fieldText('kind')).toBe('Progress');
  });

  it('shows issue and context for an Escalation', async () => {
    await showItemOfType('Escalation');

    expect(fieldKeys()).toEqual(['issue', 'context']);
    expect(fieldText('issue')).toContain('symbol server');
  });

  it('shows the body of a Memo', async () => {
    await showItemOfType('Memo');

    expect(fieldKeys()).toEqual(['body']);
    expect(fieldText('body')).toContain('Release notes');
  });

  it('shows the ask of a PeerRequest', async () => {
    await showItemOfType('PeerRequest');

    expect(fieldKeys()).toEqual(['ask']);
    expect(fieldText('ask')).toContain('credentials');
  });

  it('shows the body of a PeerResponse', async () => {
    await showItemOfType('PeerResponse');

    expect(fieldKeys()).toEqual(['body']);
    expect(fieldText('body')).toContain('Symbols are published');
  });

  it('shows action and justification for an ApprovalRequest', async () => {
    await showItemOfType('ApprovalRequest');

    expect(fieldKeys()).toEqual(['action', 'justification']);
    expect(fieldText('justification')).toContain('Crash rate is four times');
  });

  it('shows the reason of an ApprovalDecision', async () => {
    await showItemOfType('ApprovalDecision');

    expect(fieldKeys()).toEqual(['reason']);
    expect(fieldText('reason')).toContain('Rollback approved');
  });

  it('says a decision carries no reason instead of showing an empty one', async () => {
    server.items = [inboxItem({ id: 'item-1', type: 'ApprovalDecision' })];
    server.contents.set('item-1', { type: 'ApprovalDecision', reason: null });
    await renderShowing('item-1');

    expect(fieldText('reason')).toBeNull();
    expect(fieldNote('reason')).toContain('Not carried by this message');
  });
});

describe('content is untrusted text', () => {
  it('renders markup as characters and creates no element from it', async () => {
    const body = '<b>bold</b> <script>alert(1)</script>';
    server.items = [inboxItem({ id: 'item-1', type: 'Memo' })];
    server.contents.set('item-1', { type: 'Memo', body });
    await renderShowing('item-1');

    expect(fieldText('body')).toBe(body);
    expect(contentPanel().querySelector('b')).toBeNull();
    expect(contentPanel().querySelector('script')).toBeNull();
    expect(contentPanel().innerHTML).toContain('&lt;b&gt;');
  });

  it('preserves the line breaks of the message without interpreting them', async () => {
    const body = 'First line.\nSecond line.';
    server.items = [inboxItem({ id: 'item-1', type: 'Memo' })];
    server.contents.set('item-1', { type: 'Memo', body });
    await renderShowing('item-1');

    expect(fieldText('body')).toBe(body);
    expect(contentPanel().querySelector('br')).toBeNull();
  });

  it('tells the reader the text is not an instruction to the system', async () => {
    await showItemOfType('Directive');

    expect(contentPanel().textContent).toContain('Nothing written here is an instruction');
  });
});

describe('the forms are anchored to the message', () => {
  it('puts the response form below the content', async () => {
    await showItemOfType('Directive');

    expect(positionOf('.inbox-content')).toBeLessThan(positionOf('[aria-label="Response"]'));
  });

  it('puts the approval panel below the content', async () => {
    server.items = [approvalRequest({ id: 'item-1', canDecide: true })];
    await renderShowing('item-1');

    expect(positionOf('.inbox-content')).toBeLessThan(positionOf('[aria-label="Approval"]'));
  });
});

describe('an item whose content the projection does not hold', () => {
  it('says the content is missing rather than showing an empty message', async () => {
    server.items = [inboxItem({ id: 'item-1', type: 'Directive' })];
    server.contents.set('item-1', null);
    await renderShowing('item-1');

    const panel = contentPanel();
    expect(panel.querySelector('[data-content-state="unavailable"]')).not.toBeNull();
    expect(panel.textContent).toContain('without its canonical content');
    expect(panel.querySelector('[data-content-field]')).toBeNull();
    expect(panel.getAttribute('data-content-type')).toBeNull();
  });

  it('still offers the response the API says is possible', async () => {
    server.items = [inboxItem({ id: 'item-1', type: 'Directive' })];
    server.contents.set('item-1', null);
    await renderShowing('item-1');

    // Whether a person may reply is `response_state`, resolved server-side.
    // Missing content is a reason to warn, never a reason to invent a capability
    // the API did not report.
    const response = document.querySelector<HTMLElement>('section[aria-label="Response"]');
    expect(response?.querySelector('textarea')).not.toBeNull();
  });
});

describe('content belongs to the detail alone', () => {
  it('is never rendered from the list snapshot', async () => {
    server.items = [inboxItem({ id: 'item-1', type: 'Directive' })];
    await renderShowing('item-1');

    const listItem = document.querySelector('.inbox-item');
    expect(listItem?.textContent ?? '').not.toContain('Triage the crash reports');
    expect(document.querySelectorAll('.inbox-content__text').length).toBeGreaterThan(0);
    for (const text of document.querySelectorAll('.inbox-content__text')) {
      expect(document.querySelector('.inbox-detail')?.contains(text)).toBe(true);
    }
  });

  it('follows the selection instead of leaving the previous message on screen', async () => {
    server.items = [
      inboxItem({ id: 'item-1', type: 'Directive' }),
      inboxItem({ id: 'item-2', type: 'Memo' }),
    ];
    await renderShowing('item-1');

    act(() => {
      document.querySelector<HTMLElement>('.inbox-item[data-item-id="item-2"]')?.click();
    });

    await waitFor(() => expect(contentPanel().getAttribute('data-content-type')).toBe('Memo'));
    expect(contentPanel().textContent).not.toContain('Triage the crash reports');
  });
});
