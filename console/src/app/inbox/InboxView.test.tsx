// @vitest-environment jsdom

/**
 * Selection behaviour of the inbox view (US-F1-02-T12/T14).
 *
 * The property under test is the one a reader notices immediately and cannot
 * work around: the view never opens on an empty detail while there is work
 * listed, a background refresh never pulls them out of the item they are
 * reading, and an item that leaves the list — because their own action took it
 * out of the active filter — hands over to the item that took its place instead
 * of staying on screen as if it still matched.
 */

import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { InboxItem, InboxPage } from '../../api/index.js';
import type { ConsoleConfig } from '../../config.js';
import { InboxView } from './InboxView.js';

vi.mock('@microsoft/signalr', () => {
  class FakeHubConnection {
    async start(): Promise<void> {
      throw new Error('no hub in this test');
    }
    async stop(): Promise<void> {}
    async invoke(): Promise<unknown> {
      return undefined;
    }
    on(): void {}
    onreconnecting(): void {}
    onreconnected(): void {}
    onclose(): void {}
  }

  class FakeHubConnectionBuilder {
    withUrl(): this {
      return this;
    }
    withAutomaticReconnect(): this {
      return this;
    }
    configureLogging(): this {
      return this;
    }
    build(): FakeHubConnection {
      return new FakeHubConnection();
    }
  }

  return { HubConnectionBuilder: FakeHubConnectionBuilder, LogLevel: { Warning: 3 } };
});

const CONFIG: ConsoleConfig = {
  apiBaseUrl: 'https://hive.example.com',
  organizationId: 'acme-delivery',
  token: 'person-token',
  // Long enough that no poll fires during a test; refreshes are explicit here.
  pollIntervalMs: 3_600_000,
};

const GENERATED_AT = '2026-08-10T08:57:33.000Z';

function item(id: string, readState: 'Read' | 'Unread' = 'Unread'): InboxItem {
  return {
    item_id: id,
    message_id: `message-${id}`,
    assigned_position_id: 'bug-triage',
    type: 'Directive',
    origin: { type: 'Position', position_id: 'delivery-lead' },
    destination: { type: 'Position', position_id: 'bug-triage' },
    thread_id: `thread-${id}`,
    priority: 'High',
    sent_at_utc: '2026-07-12T00:00:29.000Z',
    deadline_at_utc: null,
    is_expired: false,
    reminder_state: 'None',
    last_reminder_at_utc: null,
    is_delegated: false,
    read_state: readState,
    response_state: 'AwaitingResponse',
    approval: null,
  } as InboxItem;
}

function page(items: readonly InboxItem[]): InboxPage {
  return {
    generated_at_utc: GENERATED_AT,
    last_event_applied_at_utc: GENERATED_AT,
    page_size: 25,
    next_cursor: null,
    items: [...items],
  } as InboxPage;
}

/** Items the fake API answers with, replaceable mid-test. */
let listed: readonly InboxItem[];
let warn: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.setSystemTime(new Date('2026-08-10T08:57:35.000Z'));
  warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  listed = [item('a'), item('b'), item('c')];

  vi.stubGlobal(
    'fetch',
    vi.fn((input: string) => {
      const url = String(input);

      const detailId = /\/inbox\/([^/?]+)$/.exec(url)?.[1];
      if (detailId !== undefined) {
        const found = listed.find((candidate) => candidate.item_id === detailId) ?? item(detailId);
        return Promise.resolve(
          json({
            generated_at_utc: GENERATED_AT,
            last_event_applied_at_utc: GENERATED_AT,
            item: found,
            draft_text: null,
            // Selection is a metadata property; these items model a projection
            // that holds no canonical content for them.
            content: null,
          }),
        );
      }

      if (url.includes('/read') || url.includes('/unread')) {
        const id = /\/inbox\/([^/?]+)\/(?:read|unread)$/.exec(url)?.[1] ?? '';
        return Promise.resolve(
          json({
            generated_at_utc: GENERATED_AT,
            last_event_applied_at_utc: GENERATED_AT,
            item_id: id,
            read_state: 'Read',
            response_state: 'AwaitingResponse',
            draft_text: null,
            interaction_updated_at_utc: GENERATED_AT,
          }),
        );
      }

      return Promise.resolve(json(page(listed)));
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  warn.mockRestore();
  vi.useRealTimers();
});

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json', etag: '"inbox-1"' },
  });
}

/** The item the detail panel is currently showing. */
async function shownItem(): Promise<string | null> {
  const detail = await screen.findByLabelText('Inbox item');
  return detail.getAttribute('data-item-id');
}

function listButton(id: string): HTMLElement {
  const button = document.querySelector<HTMLElement>(`.inbox-item[data-item-id="${id}"]`);
  if (button === null) {
    throw new Error(`The list does not show item ${id}.`);
  }

  return button;
}

describe('inbox selection', () => {
  it('selects the first item as soon as the inbox opens', async () => {
    render(<InboxView config={CONFIG} />);

    await waitFor(async () => expect(await shownItem()).toBe('a'));
    expect(listButton('a').getAttribute('aria-current')).toBe('true');
  });

  it('keeps the reader on the item they chose across a refresh', async () => {
    render(<InboxView config={CONFIG} />);
    await waitFor(async () => expect(await shownItem()).toBe('a'));

    fireEvent.click(listButton('c'));
    await waitFor(async () => expect(await shownItem()).toBe('c'));

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(screen.getAllByText(/Directive/).length).toBeGreaterThan(0));
    expect(await shownItem()).toBe('c');
  });

  it('moves to the item that took the place of one that left the list', async () => {
    render(<InboxView config={CONFIG} />);
    await waitFor(async () => expect(await shownItem()).toBe('a'));

    fireEvent.click(listButton('b'));
    await waitFor(async () => expect(await shownItem()).toBe('b'));

    // 'b' is read and the active filter no longer matches it.
    listed = [item('a'), item('c')];
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    await waitFor(async () => expect(await shownItem()).toBe('c'));
    expect(listButton('c').getAttribute('aria-current')).toBe('true');
  });

  it('selects the last item when the one that left was last', async () => {
    render(<InboxView config={CONFIG} />);
    await waitFor(async () => expect(await shownItem()).toBe('a'));

    fireEvent.click(listButton('c'));
    await waitFor(async () => expect(await shownItem()).toBe('c'));

    listed = [item('a'), item('b')];
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    await waitFor(async () => expect(await shownItem()).toBe('b'));
  });

  it('leaves no selection when the list becomes empty', async () => {
    render(<InboxView config={CONFIG} />);
    await waitFor(async () => expect(await shownItem()).toBe('a'));

    listed = [];
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    await waitFor(() => expect(screen.queryByLabelText('Inbox item')).toBeNull());
  });

  it('marking the selected item read under the unread filter advances to the next unread', async () => {
    render(<InboxView config={CONFIG} />);
    await waitFor(async () => expect(await shownItem()).toBe('a'));

    fireEvent.change(screen.getByLabelText('Read state'), { target: { value: 'Unread' } });
    await waitFor(async () => expect(await shownItem()).toBe('a'));

    // The action commits, the list refetches, and 'a' no longer matches.
    listed = [item('b'), item('c')];
    fireEvent.click(screen.getByRole('button', { name: 'Mark as read' }));

    await waitFor(async () => expect(await shownItem()).toBe('b'));
  });
});
