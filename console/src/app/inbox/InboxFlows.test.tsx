// @vitest-environment jsdom

/**
 * The human inbox against a stubbed public API (US-F1-02-T14).
 *
 * The real client, the real hub wrapper and the real status derivation run here
 * over a fake transport, because what is worth protecting lives in the seams
 * between them. Four properties carry most of these tests.
 *
 * - Nothing organizational is invented in the browser. Whether a person may
 *   reply comes from `response_state`, whether they may decide comes from
 *   `approval.can_decide`, and expiry is claimed only when the projection says
 *   `is_expired` — a deadline the console's own clock has passed is a different
 *   statement, and the two must not be shown as one.
 * - An emission is a request, not a fact. A reply or a decision is answered with
 *   the metadata of the emitted message, so the view reports it and refetches;
 *   it never rewrites the derived state it was given, and a governance rejection
 *   is shown in the API's own words rather than paraphrased.
 * - Emptiness is a claim that needs evidence. An inbox with nothing in it, an
 *   inbox narrowed by filters and an inbox whose projection has not reported an
 *   applied event are three different things to say.
 * - Realtime is an optimization. A notification carries no item, so it can only
 *   send the view back to REST; a replayed one changes nothing, a gap is
 *   admitted, and a hub that will not start degrades to polling instead of
 *   failing the view.
 */

import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConsoleConfig } from '../../config.js';
import {
  INBOX_NOW_UTC,
  INBOX_ORGANIZATION_ID,
  READER_POSITION_ID,
  approvalRequest,
  atOffset,
  createInboxServer,
  inboxApproval,
  inboxItem,
  inboxNotification,
  problemResponse,
} from '../testing/inboxFixture.js';
import type { InboxServer, RecordedRequest } from '../testing/inboxFixture.js';
import { hubControl } from '../testing/signalrFake.js';
import { InboxView } from './InboxView.js';

vi.mock('@microsoft/signalr', async () => {
  const fake = await import('../testing/signalrFake.js');
  return { HubConnectionBuilder: fake.FakeHubConnectionBuilder, LogLevel: fake.LogLevel };
});

const HUB = {
  subscribeInbox: 'SubscribeToInbox',
  inboxChanged: 'InboxChanged',
} as const;

/**
 * Poll interval long enough that no poll fires during a test: everything except
 * the fallback suite exercises the hub, and a background poll would make request
 * counts a property of how long an assertion took to settle.
 */
const CONFIG: ConsoleConfig = {
  apiBaseUrl: 'https://hive.example.com',
  organizationId: INBOX_ORGANIZATION_ID,
  token: 'person-token',
  pollIntervalMs: 3_600_000,
};

/** Same console with a poll interval the fallback suite can advance past. */
const POLLING_CONFIG: ConsoleConfig = { ...CONFIG, pollIntervalMs: 5_000 };

let server: InboxServer;
let warn: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.setSystemTime(new Date(INBOX_NOW_UTC));
  hubControl.reset();
  warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);

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

/** Renders the inbox and waits for the first snapshot to be on screen. */
async function renderInbox(config: ConsoleConfig = CONFIG): Promise<HTMLElement> {
  const { container } = render(<InboxView config={config} />);
  await waitFor(() => expect(server.listRequests().length).toBeGreaterThan(0));
  return container;
}

/** Renders and waits until the given item is listed and selected. */
async function renderInboxShowing(
  itemId: string,
  config: ConsoleConfig = CONFIG,
): Promise<HTMLElement> {
  const container = await renderInbox(config);
  await waitFor(() => expect(shownItemId()).toBe(itemId));
  return container;
}

function listButton(itemId: string): HTMLElement {
  const button = document.querySelector<HTMLElement>(`.inbox-item[data-item-id="${itemId}"]`);
  if (button === null) {
    throw new Error(`The list does not show item ${itemId}.`);
  }

  return button;
}

function listedItemIds(): string[] {
  return [...document.querySelectorAll('.inbox-item')].map(
    (element) => element.getAttribute('data-item-id') ?? '',
  );
}

/** The item the detail panel is currently showing, if any. */
function shownItemId(): string | null {
  return document.querySelector('.inbox-detail')?.getAttribute('data-item-id') ?? null;
}

function detailPanel(): HTMLElement {
  const panel = document.querySelector<HTMLElement>('.inbox-detail');
  if (panel === null) {
    throw new Error('No item detail is on screen.');
  }

  return panel;
}

/**
 * A panel of the detail, by its landmark label. Scoped to the section rather
 * than looked up with `getByLabelText`, which would also match the filter bar's
 * «Response» select — the filters and the reply form share the word legitimately.
 */
function panel(label: 'Response' | 'Approval'): HTMLElement {
  const section = document.querySelector<HTMLElement>(`section[aria-label="${label}"]`);
  if (section === null) {
    throw new Error(`The detail shows no ${label} panel.`);
  }

  return section;
}

function notice(id: string): HTMLElement | null {
  return document.querySelector<HTMLElement>(`[data-notice="${id}"]`);
}

function outcomeText(): string {
  return document.querySelector('.inbox-outcome')?.textContent ?? '';
}

function errorText(): string {
  return document.querySelector('.inbox-error')?.textContent ?? '';
}

/**
 * Value of a labelled fact in a `dl`. Timestamps are formatted for the reader's
 * locale, so tests read the row and assert what it says about it rather than
 * pinning a rendering that depends on where the suite runs.
 */
function factOf(scope: HTMLElement, label: string): string | null {
  for (const row of scope.querySelectorAll('.inbox-facts > div')) {
    if (row.querySelector('dt')?.textContent === label) {
      return row.querySelector('dd')?.textContent ?? null;
    }
  }

  return null;
}

function deadlineBadgeOf(element: HTMLElement): HTMLElement | null {
  return element.querySelector<HTMLElement>('[data-deadline]');
}

function lastRequestTo(suffix: string): RecordedRequest {
  const request = server.requestsTo(suffix).at(-1);
  if (request === undefined) {
    throw new Error(`The console never called ${suffix}.`);
  }

  return request;
}

async function subscribed(): Promise<void> {
  await waitFor(() =>
    expect(hubControl.connection().callsTo(HUB.subscribeInbox)).toContain(INBOX_ORGANIZATION_ID),
  );
}

/** Delivers a hub notification the way the server would push it. */
function pushNotification(sequence: number, itemId = 'item-new'): void {
  act(() => {
    hubControl.connection().emit(HUB.inboxChanged, inboxNotification({ sequence, itemId }));
  });
}

describe('an inbox with nothing to show', () => {
  it('states that nothing is addressed to the person, rather than failing', async () => {
    const container = await renderInbox();

    await screen.findByText('Your inbox is empty');
    expect(container.querySelector('.panel--error')).toBeNull();
    expect(screen.queryByLabelText('Inbox items')).toBeNull();
    // No detail either: there is no item to be reading.
    expect(shownItemId()).toBeNull();
  });

  it('says a filter is hiding the inbox instead of calling the inbox empty', async () => {
    server.items = [inboxItem({ id: 'directive-1' })];
    await renderInboxShowing('directive-1');

    server.items = [];
    fireEvent.change(screen.getByLabelText('Read state'), { target: { value: 'Read' } });

    await screen.findByText('No item matches these filters');
    expect(screen.queryByText('Your inbox is empty')).toBeNull();
    expect(screen.getByText(/whole matching inbox and not an exhausted page/)).toBeDefined();
  });

  it('refuses to present emptiness as a fact while the projection is silent', async () => {
    server.projectionAppliedAtUtc = null;
    await renderInbox();

    await screen.findByText('Inbox data may be incomplete');
    expect(notice('projection-not-started')).not.toBeNull();
    expect(screen.queryByText('Your inbox is empty')).toBeNull();
  });
});

describe('messages assigned to the person', () => {
  beforeEach(() => {
    server.items = [
      inboxItem({
        id: 'directive-1',
        type: 'Directive',
        origin: 'delivery-lead',
        priority: 'Critical',
        deadlineAtUtc: atOffset(4 * 60 * 60 * 1_000),
      }),
      inboxItem({ id: 'peer-1', type: 'PeerRequest', origin: 'runtime-lead', priority: 'Low' }),
      inboxItem({ id: 'memo-1', type: 'Memo', responseState: 'NotApplicable', readState: 'Read' }),
    ];
  });

  it('lists every assigned item in the order the API fixed', async () => {
    await renderInboxShowing('directive-1');

    expect(listedItemIds()).toEqual(['directive-1', 'peer-1', 'memo-1']);
    expect(document.querySelector('.organogram__summary')?.textContent).toMatch(
      /^3 items · Up to date · snapshot /,
    );
  });

  it('shows what each item is, who sent it and which position it is addressed to', async () => {
    await renderInboxShowing('directive-1');

    const directive = within(listButton('directive-1'));
    expect(directive.getByText('Directive')).toBeDefined();
    expect(directive.getByText('Critical')).toBeDefined();
    expect(directive.getByText(`From delivery-lead · to ${READER_POSITION_ID}`)).toBeDefined();

    expect(within(listButton('peer-1')).getByText('Peer request')).toBeDefined();
    expect(listButton('memo-1').getAttribute('data-read-state')).toBe('Read');
    expect(listButton('directive-1').getAttribute('data-read-state')).toBe('Unread');
  });

  it('shows the correlation of the selected item in its detail', async () => {
    await renderInboxShowing('directive-1');
    const detail = within(detailPanel());

    expect(detail.getByText('thread-directive-1')).toBeDefined();
    expect(detail.getByText('message-directive-1')).toBeDefined();
    expect(detail.getByText('delivery-lead')).toBeDefined();
    expect(factOf(detailPanel(), 'Assigned position')).toBe(READER_POSITION_ID);
    expect(factOf(detailPanel(), 'Sent')).not.toBe('—');
  });

  it('marks an item read against the API and lets the refetched list say so', async () => {
    await renderInboxShowing('directive-1');

    fireEvent.click(screen.getByRole('button', { name: 'Mark as read' }));

    await waitFor(() => expect(server.requestsTo('/read')).toHaveLength(1));
    expect(lastRequestTo('/read').method).toBe('POST');
    await waitFor(() =>
      expect(listButton('directive-1').getAttribute('data-read-state')).toBe('Read'),
    );
  });

  it('filters through the API rather than narrowing the page in hand', async () => {
    await renderInboxShowing('directive-1');

    fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'ApprovalRequest' } });

    await waitFor(() => {
      const query = server.listRequests().at(-1)?.query;
      expect(query?.get('type')).toBe('ApprovalRequest');
      expect(query?.get('page_size')).toBe('25');
    });
  });

  it('talks only to the public versioned surface, always with the person credential', async () => {
    await renderInboxShowing('directive-1');
    await subscribed();

    expect(hubControl.urls).toEqual(['https://hive.example.com/api/v1/organization-updates']);
    expect(server.requests.length).toBeGreaterThan(0);
    for (const request of server.requests) {
      expect(
        request.url.startsWith(
          `https://hive.example.com/api/v1/organizations/${INBOX_ORGANIZATION_ID}/`,
        ),
      ).toBe(true);
      expect(request.path).not.toContain('/internal');
      expect(request.headers['authorization']).toBe('Bearer person-token');
    }
  });
});

describe('answering as the occupied position', () => {
  beforeEach(() => {
    server.items = [
      inboxItem({ id: 'directive-1', type: 'Directive' }),
      inboxItem({ id: 'peer-1', type: 'PeerRequest' }),
    ];
  });

  function type(text: string): void {
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: text } });
  }

  it('names the canonical message the position will emit, and who emits it', async () => {
    await renderInboxShowing('directive-1');

    const form = within(panel('Response'));
    expect(form.getByText(READER_POSITION_ID)).toBeDefined();
    expect(form.getByText('Report')).toBeDefined();
    expect(form.getByText(/validated and audited by the organization, not composed here/)).toBeDefined();
  });

  it('sends a reply to a Directive as a Report of the kind the person chose', async () => {
    await renderInboxShowing('directive-1');
    const listCallsBefore = server.listRequests().length;
    const detailCallsBefore = server.requestsTo('/inbox/directive-1').length;

    type('Triage finished for the reported crash.');
    fireEvent.click(screen.getByLabelText('Completed'));
    fireEvent.click(screen.getByRole('button', { name: 'Send response' }));

    await waitFor(() => expect(server.requestsTo('/reply')).toHaveLength(1));
    expect(lastRequestTo('/reply').body).toEqual({
      body: 'Triage finished for the reported crash.',
      report_kind: 'done',
    });

    // The emission is reported as what it is, and the view goes back to the API
    // for the state instead of assuming the projection has caught up.
    await waitFor(() => expect(outcomeText()).toContain('accepted for emission'));
    expect(outcomeText()).toContain('emitted-directive-1');
    await waitFor(() => expect(server.listRequests().length).toBeGreaterThan(listCallsBefore));
    expect(server.requestsTo('/inbox/directive-1').length).toBeGreaterThan(detailCallsBefore);
  });

  it('sends no report kind for a message kind whose reply has none', async () => {
    await renderInboxShowing('directive-1');

    fireEvent.click(listButton('peer-1'));
    await waitFor(() => expect(shownItemId()).toBe('peer-1'));
    expect(within(panel('Response')).getByText('Peer response')).toBeDefined();
    expect(screen.queryByLabelText('Progress')).toBeNull();

    type('Yes, the fix is already on main.');
    fireEvent.click(screen.getByRole('button', { name: 'Send response' }));

    await waitFor(() => expect(server.requestsTo('/reply')).toHaveLength(1));
    expect(lastRequestTo('/reply').body).toEqual({ body: 'Yes, the fix is already on main.' });
  });

  it('keeps the draft on the server, not in the browser', async () => {
    await renderInboxShowing('directive-1');

    type('Half-written thought.');
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));

    await waitFor(() => expect(server.requestsTo('/draft')).toHaveLength(1));
    expect(lastRequestTo('/draft').body).toEqual({ body: 'Half-written thought.' });

    fireEvent.click(listButton('peer-1'));
    await waitFor(() => expect(shownItemId()).toBe('peer-1'));
    expect((screen.getByLabelText('Message') as HTMLTextAreaElement).value).toBe('');

    fireEvent.click(listButton('directive-1'));
    await waitFor(() => expect(shownItemId()).toBe('directive-1'));
    // The draft comes back because the server holds it, not the component.
    await waitFor(() =>
      expect((screen.getByLabelText('Message') as HTMLTextAreaElement).value).toBe(
        'Half-written thought.',
      ),
    );
  });

  it('offers no response for a message kind the mapping does not answer', async () => {
    server.items = [inboxItem({ id: 'memo-1', type: 'Memo', responseState: 'NotApplicable' })];
    await renderInboxShowing('memo-1');

    expect(
      screen.getByText('This message kind has no correlated response in the current mapping.'),
    ).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Send response' })).toBeNull();
  });

  it('offers no second response once one was emitted in the thread', async () => {
    server.items = [inboxItem({ id: 'directive-1', responseState: 'Responded' })];
    await renderInboxShowing('directive-1');

    expect(
      screen.getByText('A correlated response has already been emitted in this thread.'),
    ).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Send response' })).toBeNull();
  });

  it('reports a rejected emission verbatim and claims nothing was emitted', async () => {
    server.reply = () =>
      problemResponse(422, 'The message was rejected', {
        detail: 'The organizational pipeline refused the message.',
        errors: [
          { code: 'thread_mismatch', path: '$.thread_id', reason: 'The thread is already closed.' },
        ],
      });
    await renderInboxShowing('directive-1');

    type('Reporting progress.');
    fireEvent.click(screen.getByRole('button', { name: 'Send response' }));

    await waitFor(() => expect(errorText()).toContain('The message was rejected'));
    expect(errorText()).toContain('thread_mismatch');
    expect(errorText()).toContain('$.thread_id');
    expect(errorText()).toContain('The thread is already closed.');
    expect(outcomeText()).toBe('');
    // The text is not thrown away on a rejection the person may want to amend.
    expect((screen.getByLabelText('Message') as HTMLTextAreaElement).value).toBe(
      'Reporting progress.',
    );
  });
});

describe('deciding an approval the policy assigned to this position', () => {
  beforeEach(() => {
    server.items = [approvalRequest({ id: 'approval-1', canDecide: true })];
  });

  it('shows what is being approved and under which policy', async () => {
    await renderInboxShowing('approval-1');
    const approval = within(panel('Approval'));

    expect(approval.getByText('Deploy hotfix 4.2.1 to production')).toBeDefined();
    expect(approval.getByText('policies/deployment-approval')).toBeDefined();
    expect(approval.getByText('request-approval-1')).toBeDefined();
    expect(approval.getByText('Awaiting decision')).toBeDefined();
  });

  it('emits an approval with the reason the person recorded', async () => {
    await renderInboxShowing('approval-1');

    fireEvent.change(screen.getByLabelText('Reason (optional)'), {
      target: { value: 'Rollback plan verified.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(server.requestsTo('/decision')).toHaveLength(1));
    expect(lastRequestTo('/decision').body).toEqual({
      approved: true,
      reason: 'Rollback plan verified.',
    });
    await waitFor(() => expect(outcomeText()).toContain('The decision was accepted'));
    expect(outcomeText()).toContain('approved');
  });

  it('emits a rejection, and sends no reason when none was given', async () => {
    await renderInboxShowing('approval-1');

    fireEvent.click(screen.getByRole('button', { name: 'Reject' }));

    await waitFor(() => expect(server.requestsTo('/decision')).toHaveLength(1));
    expect(lastRequestTo('/decision').body).toEqual({ approved: false });
    await waitFor(() => expect(outcomeText()).toContain('rejected'));
  });

  it('leaves the approval state to the projection instead of writing it locally', async () => {
    await renderInboxShowing('approval-1');
    const listCallsBefore = server.listRequests().length;

    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(outcomeText()).toContain('The decision was accepted'));
    // The server still reports the request as pending, and so does the console.
    expect(
      within(listButton('approval-1')).getByText('Awaiting decision'),
    ).toBeDefined();
    expect(outcomeText()).toContain('once the projection applies');
    await waitFor(() => expect(server.listRequests().length).toBeGreaterThan(listCallsBefore));
  });
});

describe('an approval this person may not decide', () => {
  it('offers no decision when the policy names another approver', async () => {
    server.items = [approvalRequest({ id: 'approval-1', canDecide: false })];
    await renderInboxShowing('approval-1');

    expect(
      screen.getByText('The approval policy does not name this position as the approver.'),
    ).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Approve' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Reject' })).toBeNull();
    expect(screen.queryByLabelText('Reason (optional)')).toBeNull();
  });

  it('distinguishes a decided request from a closed window', async () => {
    server.items = [
      approvalRequest({ id: 'approval-1', canDecide: false, state: 'Approved' }),
      approvalRequest({ id: 'approval-2', canDecide: false, state: 'Expired' }),
    ];
    await renderInboxShowing('approval-1');

    expect(screen.getByText('This request was already approved.')).toBeDefined();

    fireEvent.click(listButton('approval-2'));
    await waitFor(() => expect(shownItemId()).toBe('approval-2'));
    expect(screen.getByText('The approval window for this request has closed.')).toBeDefined();
  });

  it('reports a governance rejection of a decision the console had shown as possible', async () => {
    server.items = [approvalRequest({ id: 'approval-1', canDecide: true })];
    server.decide = () =>
      problemResponse(403, 'The decision was refused', {
        errors: [
          {
            code: 'approver_not_authorized',
            path: '$.from_position_id',
            reason: 'The position is not an approver for this policy.',
          },
        ],
      });
    await renderInboxShowing('approval-1');

    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(errorText()).toContain('The decision was refused'));
    expect(errorText()).toContain('approver_not_authorized');
    expect(errorText()).toContain('The position is not an approver for this policy.');
    expect(outcomeText()).toBe('');
    // Authority is the server's answer: a refusal leaves the request pending.
    expect(within(listButton('approval-1')).getByText('Awaiting decision')).toBeDefined();
  });

  it('shows no approval panel on an item that is not an approval request', async () => {
    server.items = [inboxItem({ id: 'directive-1' })];
    await renderInboxShowing('directive-1');

    expect(screen.queryByLabelText('Approval')).toBeNull();
  });
});

describe('deadlines and reminders', () => {
  it('separates a scheduled deadline from an imminent one', async () => {
    server.items = [
      inboxItem({ id: 'later', deadlineAtUtc: atOffset(4 * 60 * 60 * 1_000) }),
      inboxItem({ id: 'soon', deadlineAtUtc: atOffset(45 * 1_000) }),
      inboxItem({ id: 'undated' }),
    ];
    await renderInboxShowing('later');

    expect(deadlineBadgeOf(listButton('later'))?.getAttribute('data-deadline')).toBe('scheduled');
    expect(deadlineBadgeOf(listButton('later'))?.textContent).toBe('Due in 4h 0m');
    expect(deadlineBadgeOf(listButton('soon'))?.getAttribute('data-deadline')).toBe('due-soon');
    expect(deadlineBadgeOf(listButton('undated'))).toBeNull();
  });

  it('does not call an item expired just because its deadline passed', async () => {
    server.items = [
      inboxItem({ id: 'overdue', deadlineAtUtc: atOffset(-10 * 60 * 1_000) }),
      inboxItem({ id: 'expired', deadlineAtUtc: atOffset(-30 * 60 * 1_000), isExpired: true }),
    ];
    await renderInboxShowing('overdue');

    const overdue = listButton('overdue');
    expect(deadlineBadgeOf(overdue)?.getAttribute('data-deadline')).toBe('due-now');
    expect(deadlineBadgeOf(overdue)?.textContent).toBe('Due 10m ago');
    expect(within(overdue).queryByText('Expired')).toBeNull();

    // Only the projection declares expiry, and when it does the console says so.
    expect(deadlineBadgeOf(listButton('expired'))?.getAttribute('data-deadline')).toBe('expired');
    expect(within(listButton('expired')).getByText('Expired')).toBeDefined();
  });

  it('stops calling a deadline imminent once the clock passes it, with no server event', async () => {
    server.items = [inboxItem({ id: 'soon', deadlineAtUtc: atOffset(45 * 1_000) })];
    await renderInboxShowing('soon');
    expect(deadlineBadgeOf(listButton('soon'))?.getAttribute('data-deadline')).toBe('due-soon');

    const requestsBefore = server.requests.length;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });

    await waitFor(() =>
      expect(deadlineBadgeOf(listButton('soon'))?.getAttribute('data-deadline')).toBe('due-now'),
    );
    // Time passing is not news from the server: nothing was refetched.
    expect(server.requests.length).toBe(requestsBefore);
  });

  it('shows that a reminder was sent, when it was sent, and that an item is delegated', async () => {
    server.items = [
      inboxItem({
        id: 'reminded',
        deadlineAtUtc: atOffset(20 * 60 * 1_000),
        reminderState: 'Sent',
        lastReminderAtUtc: atOffset(-5 * 60 * 1_000),
        isDelegated: true,
      }),
    ];
    await renderInboxShowing('reminded');

    expect(within(listButton('reminded')).getByText('Reminder sent')).toBeDefined();
    expect(within(listButton('reminded')).getByText('Delegated')).toBeDefined();

    // The reminder is reported with the instant it was sent, not merely as a flag.
    const sentAt = factOf(detailPanel(), 'Reminder');
    expect(sentAt).not.toBeNull();
    expect(sentAt).not.toBe('—');
  });

  it('reports an expired deadline in the detail as well as on the badge', async () => {
    server.items = [
      inboxItem({
        id: 'expired',
        deadlineAtUtc: atOffset(-30 * 60 * 1_000),
        isExpired: true,
        approval: inboxApproval({ canDecide: false, state: 'Expired' }),
        type: 'ApprovalRequest',
        responseState: 'NotApplicable',
      }),
    ];
    await renderInboxShowing('expired');

    expect(factOf(detailPanel(), 'Deadline')).toMatch(/ · expired$/);
    expect(screen.getByText('The approval window for this request has closed.')).toBeDefined();
  });
});

describe('keeping the inbox current', () => {
  beforeEach(() => {
    server.items = [inboxItem({ id: 'directive-1' })];
  });

  it('subscribes to its own inbox and refetches instead of trusting the hub', async () => {
    const container = await renderInboxShowing('directive-1');

    await subscribed();
    // Subscribing is not data: it sends the view back to REST.
    await waitFor(() => expect(server.listRequests().length).toBe(2));
    expect(container.querySelector('.notices')).toBeNull();
  });

  it('goes back to the API when a change is notified, since the event carries no item', async () => {
    await renderInboxShowing('directive-1');
    await subscribed();
    await waitFor(() => expect(server.listRequests().length).toBe(2));

    server.items = [...server.items, inboxItem({ id: 'directive-2', origin: 'runtime-lead' })];
    pushNotification(7, 'directive-2');

    await waitFor(() => expect(listedItemIds()).toEqual(['directive-1', 'directive-2']));
    expect(server.listRequests().length).toBe(3);
  });

  it('ignores a replayed notification instead of refetching again', async () => {
    await renderInboxShowing('directive-1');
    await subscribed();
    await waitFor(() => expect(server.listRequests().length).toBe(2));

    pushNotification(7);
    await waitFor(() => expect(server.listRequests().length).toBe(3));

    pushNotification(4);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(100);
    });
    expect(server.listRequests().length).toBe(3);
  });

  it('admits a gap in the notifications rather than quietly papering over it', async () => {
    await renderInboxShowing('directive-1');
    await subscribed();

    pushNotification(7);
    await waitFor(() => expect(notice('inbox-missed-notifications')).toBeNull());

    const before = server.listRequests().length;
    pushNotification(11);

    // The gap is worth saying precisely because the recovery hides it: the
    // notice has to outlive the snapshot that recovered from it, or a reader
    // never learns that anything was missed.
    await waitFor(() => expect(server.listRequests().length).toBeGreaterThan(before));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(50);
    });
    expect(notice('inbox-missed-notifications')).not.toBeNull();
    // The list is still current: the gap cannot be reconstructed, the snapshot can.
    expect(listedItemIds()).toEqual(['directive-1']);
  });

  it('resubscribes and refetches after the connection is restored', async () => {
    await renderInboxShowing('directive-1');
    await subscribed();
    const before = server.listRequests().length;

    act(() => hubControl.connection().dropConnection());

    await waitFor(() => expect(notice('reconnecting')).not.toBeNull());
    // The inbox is not taken away while the transport is being restored.
    expect(listedItemIds()).toEqual(['directive-1']);

    act(() => hubControl.connection().restoreConnection());

    await waitFor(() =>
      expect(hubControl.connection().callsTo(HUB.subscribeInbox)).toEqual([
        INBOX_ORGANIZATION_ID,
        INBOX_ORGANIZATION_ID,
      ]),
    );
    await waitFor(() => expect(server.listRequests().length).toBeGreaterThan(before));
    await waitFor(() => expect(notice('reconnecting')).toBeNull());
  });

  it('holds a change back while the reader has paged, instead of rebuilding under them', async () => {
    server.items = Array.from({ length: 30 }, (_, index) =>
      inboxItem({ id: `item-${String(index).padStart(2, '0')}` }),
    );
    await renderInboxShowing('item-00');
    await subscribed();
    await waitFor(() => expect(listedItemIds()).toHaveLength(25));

    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(listedItemIds()).toHaveLength(30));
    const afterPaging = server.listRequests().length;

    pushNotification(7);

    await waitFor(() => expect(notice('inbox-pending-update')).not.toBeNull());
    expect(listedItemIds()).toHaveLength(30);
    expect(server.listRequests().length).toBe(afterPaging);

    fireEvent.click(within(notice('inbox-pending-update')!).getByRole('button'));

    await waitFor(() => expect(listedItemIds()).toHaveLength(25));
    expect(notice('inbox-pending-update')).toBeNull();
  });
});

describe('an inbox without a live connection', () => {
  beforeEach(() => {
    hubControl.startError = new Error('hub unreachable');
    server.items = [inboxItem({ id: 'directive-1' })];
  });

  it('degrades to controlled polling and says so, rather than failing the view', async () => {
    const container = await renderInboxShowing('directive-1', POLLING_CONFIG);

    await waitFor(() => expect(notice('polling')).not.toBeNull());
    expect(notice('polling')?.textContent).toContain('polled every 5s');
    expect(container.querySelector('.panel--error')).toBeNull();
    expect(listedItemIds()).toEqual(['directive-1']);
  });

  it('polls conditionally and applies what the server says changed', async () => {
    await renderInboxShowing('directive-1', POLLING_CONFIG);
    await waitFor(() => expect(notice('polling')).not.toBeNull());

    await act(async () => {
      await vi.advanceTimersByTimeAsync(POLLING_CONFIG.pollIntervalMs);
    });

    await waitFor(() => expect(server.listRequests().length).toBeGreaterThan(1));
    expect(server.listRequests().at(-1)?.headers['if-none-match']).toBe('"inbox-1"');
    // An unchanged answer is still an agreement: the list stays as it is.
    expect(listedItemIds()).toEqual(['directive-1']);

    server.items = [...server.items, inboxItem({ id: 'directive-2' })];
    server.version += 1;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(POLLING_CONFIG.pollIntervalMs);
    });

    await waitFor(() => expect(listedItemIds()).toEqual(['directive-1', 'directive-2']));
  });

  it('keeps the inbox on screen and admits when an update attempt fails', async () => {
    await renderInboxShowing('directive-1', POLLING_CONFIG);
    await waitFor(() => expect(notice('polling')).not.toBeNull());

    server.list = () => Promise.reject(new TypeError('Failed to fetch'));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(POLLING_CONFIG.pollIntervalMs);
    });

    await waitFor(() => expect(notice('update-failed')).not.toBeNull());
    expect(listedItemIds()).toEqual(['directive-1']);
    expect(document.querySelector('.panel--error')).toBeNull();
  });

  it('warns that the inbox may be out of date once updates stop succeeding', async () => {
    await renderInboxShowing('directive-1', POLLING_CONFIG);
    await waitFor(() => expect(notice('polling')).not.toBeNull());

    server.list = () => Promise.reject(new TypeError('Failed to fetch'));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });

    await waitFor(() => expect(notice('stale')).not.toBeNull());
  });
});

describe('an inbox the API will not answer', () => {
  it('names a credential with no person bound to it without revealing an organization', async () => {
    server.list = () => problemResponse(404, 'Inbox not found');
    render(<InboxView config={CONFIG} />);

    await screen.findByText('No inbox is visible to this credential');
    expect(screen.getByText(/person binding is what is missing/)).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull();
  });

  it('treats a projection that is not materialized yet as retryable, and recovers', async () => {
    const answer = server.list;
    server.list = () => problemResponse(503, 'Read model unavailable');
    render(<InboxView config={CONFIG} />);

    await screen.findByText('The inbox read model is not ready');

    server.items = [inboxItem({ id: 'directive-1' })];
    server.list = answer;
    fireEvent.click(screen.getAllByRole('button', { name: 'Try again' })[0]!);

    await waitFor(() => expect(listedItemIds()).toEqual(['directive-1']));
    expect(screen.queryByText('The inbox read model is not ready')).toBeNull();
  });
});
