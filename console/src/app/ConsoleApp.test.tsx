// @vitest-environment jsdom

/**
 * End-to-end tests of the console shell over a stubbed public API (US-F1-01-T14).
 *
 * These exercise the real client, the real hub protocol wrapper and the real
 * status derivation against a fake transport, because the properties worth
 * protecting live in the seams between them: a subscription refetches the REST
 * snapshot instead of trusting the hub, a notification only ever moves a
 * position forward by sequence, a hub that will not start degrades to controlled
 * polling rather than failing the view, and a snapshot already on screen is
 * never replaced by an error page. The console also has to keep saying which of
 * those modes it is in — a stale organogram presented as live is the one failure
 * a reader cannot detect.
 */

import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { OrganizationPositionState, PositionStatesResponse } from '../api/index.js';
import type { ConsoleConfig } from '../config.js';
import { ConsoleApp } from './ConsoleApp.js';
import { hubControl } from './testing/signalrFake.js';
import {
  ORGANIZATION_ID,
  deliveryOrganization,
  position,
  positionState,
  positionStatesResponse,
  snapshot,
  unit,
} from './testing/organogramFixture.js';

/**
 * The SignalR transport is replaced by the shared double, which is shaped after
 * the parts of the connection the client actually uses so its own subscription
 * and reconnection semantics stay under test.
 */
vi.mock('@microsoft/signalr', async () => {
  const fake = await import('./testing/signalrFake.js');
  return { HubConnectionBuilder: fake.FakeHubConnectionBuilder, LogLevel: fake.LogLevel };
});

const SUBSCRIBE = 'SubscribeToOrganization';

const HUB_EVENTS = {
  organogramChanged: 'OrganogramChanged',
  positionStateChanged: 'PositionStateChanged',
} as const;

const CONFIG: ConsoleConfig = {
  apiBaseUrl: 'https://hive.example.com',
  organizationId: ORGANIZATION_ID,
  token: 'read-only-token',
  pollIntervalMs: 5_000,
};

interface RecordedRequest {
  readonly url: string;
  readonly headers: Record<string, string>;
}

interface Server {
  readonly requests: RecordedRequest[];
  organogram(request: RecordedRequest): Promise<Response>;
  positionStates(request: RecordedRequest): Promise<Response>;
  organogramRequests(): RecordedRequest[];
  positionStateRequests(): RecordedRequest[];
}

let server: Server;
let fetchMock: ReturnType<typeof vi.fn>;
let warn: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  // Pinned close to the snapshot timestamp so freshness is a property of the
  // test rather than of the day it runs on.
  vi.setSystemTime(new Date('2026-08-03T10:00:05.000Z'));
  hubControl.reset();
  warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);

  const requests: RecordedRequest[] = [];
  server = {
    requests,
    organogram: () => Promise.resolve(jsonResponse(deliveryOrganization())),
    positionStates: () => Promise.resolve(notModified('"v7-0"')),
    organogramRequests: () => requests.filter((request) => request.url.endsWith('/organogram')),
    positionStateRequests: () =>
      requests.filter((request) => request.url.endsWith('/position-states')),
  };

  fetchMock = vi.fn((input: string, init?: RequestInit) => {
    const request: RecordedRequest = {
      url: String(input),
      headers: (init?.headers ?? {}) as Record<string, string>,
    };
    requests.push(request);
    return request.url.endsWith('/position-states')
      ? server.positionStates(request)
      : server.organogram(request);
  });
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  warn.mockRestore();
  vi.useRealTimers();
});

function jsonResponse(body: unknown, init: ResponseInit = {}): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    ...init,
    headers: { 'content-type': 'application/json', ...(init.headers ?? {}) },
  });
}

function problemResponse(status: number, title: string): Response {
  return new Response(JSON.stringify({ status, title }), {
    status,
    headers: { 'content-type': 'application/problem+json' },
  });
}

function notModified(etag: string): Response {
  return new Response(null, { status: 304, headers: { etag } });
}

function statesResponse(
  states: readonly OrganizationPositionState[],
  etag: string,
  overrides: Partial<PositionStatesResponse> = {},
): Response {
  return jsonResponse(positionStatesResponse(states, overrides), { headers: { etag } });
}

/** Renders the console and waits for the first snapshot to be on screen. */
async function renderConsole(): Promise<HTMLElement> {
  const { container } = render(<ConsoleApp config={CONFIG} />);
  await screen.findByText('Head of Delivery');
  return container;
}

function stateOf(container: HTMLElement, positionId: string): string | null {
  return (
    container
      .querySelector(`[data-position-id="${positionId}"] [data-state]`)
      ?.getAttribute('data-state') ?? null
  );
}

function notice(container: HTMLElement, id: string): HTMLElement | null {
  return container.querySelector(`[data-notice="${id}"]`);
}

function channelOf(container: HTMLElement): string | null {
  return container.querySelector('.update-indicator')?.getAttribute('data-channel') ?? null;
}

describe('ConsoleApp over a live subscription', () => {
  it('renders the organogram from the public API and reports itself as live', async () => {
    const container = await renderConsole();

    await waitFor(() => expect(channelOf(container)).toBe('live'));
    expect(within(container).getByText('Live')).toBeDefined();
    expect(container.querySelector('.notices')).toBeNull();
    expect(stateOf(container, 'platform-lead')).toBe('Working');
  });

  it('talks only to the public versioned surface, never to /internal', async () => {
    await renderConsole();

    await waitFor(() => expect(hubControl.urls).toHaveLength(1));
    expect(hubControl.urls[0]).toBe('https://hive.example.com/api/v1/organization-updates');
    expect(server.requests.length).toBeGreaterThan(0);
    for (const request of server.requests) {
      expect(request.url.startsWith(`https://hive.example.com/api/v1/organizations/${ORGANIZATION_ID}`)).toBe(true);
      expect(request.url).not.toContain('/internal');
      expect(request.headers['authorization']).toBe('Bearer read-only-token');
    }
  });

  it('subscribes explicitly and refetches the snapshot instead of trusting the hub', async () => {
    await renderConsole();

    await waitFor(() => expect(hubControl.connection().callsTo(SUBSCRIBE)).toEqual([ORGANIZATION_ID]));
    // The subscription is not a source of truth: it sends the view back to REST.
    await waitFor(() => expect(server.organogramRequests().length).toBe(2));
  });

  it('moves a position to the state a notification carries, and leaves the rest alone', async () => {
    const container = await renderConsole();
    await waitFor(() => expect(channelOf(container)).toBe('live'));

    act(() => {
      hubControl.connection().emit(HUB_EVENTS.positionStateChanged, {
        organization_id: ORGANIZATION_ID,
        state: positionState({ positionId: 'runtime-lead', state: 'Working', sequence: 12 }),
      });
    });

    await waitFor(() => expect(stateOf(container, 'runtime-lead')).toBe('Working'));
    expect(stateOf(container, 'platform-lead')).toBe('Working');
    expect(stateOf(container, 'head-of-delivery')).toBe('WaitingHuman');
    // A live view is derived from the snapshot plus notifications; no refetch.
    expect(server.organogramRequests()).toHaveLength(2);
  });

  it('discards a replayed notification that would move a position backwards', async () => {
    const container = await renderConsole();
    await waitFor(() => expect(channelOf(container)).toBe('live'));

    act(() => {
      hubControl.connection().emit(HUB_EVENTS.positionStateChanged, {
        organization_id: ORGANIZATION_ID,
        state: positionState({ positionId: 'runtime-lead', state: 'Idle', sequence: 12 }),
      });
    });
    await waitFor(() => expect(stateOf(container, 'runtime-lead')).toBe('Idle'));

    act(() => {
      hubControl.connection().emit(HUB_EVENTS.positionStateChanged, {
        organization_id: ORGANIZATION_ID,
        state: positionState({ positionId: 'runtime-lead', state: 'Blocked', sequence: 4 }),
      });
    });

    expect(stateOf(container, 'runtime-lead')).toBe('Idle');
  });

  it('refetches the organogram when the registry publishes a new version', async () => {
    const container = await renderConsole();
    await waitFor(() => expect(channelOf(container)).toBe('live'));

    let release: (() => void) | null = null;
    server.organogram = () =>
      new Promise<Response>((resolve) => {
        release = () =>
          resolve(
            jsonResponse(
              snapshot({
                registry: { version: 8, fingerprint: 'd4e5f6' },
                units: [
                  unit('delivery', null, 'head-of-delivery', 'Delivery'),
                  unit('growth', 'delivery', 'growth-lead', 'Growth'),
                ],
                positions: [
                  position({ id: 'head-of-delivery', name: 'Head of Delivery', unitId: 'delivery' }),
                  position({ id: 'growth-lead', name: 'Growth Lead', unitId: 'growth' }),
                ],
              }),
            ),
          );
      });

    act(() => {
      hubControl.connection().emit(HUB_EVENTS.organogramChanged, {
        organization_id: ORGANIZATION_ID,
        registry: { version: 8, fingerprint: 'd4e5f6' },
        changed_at_utc: '2026-08-03T10:00:04.000Z',
      });
    });

    // While the refetch is in flight the previous organogram stays on screen and
    // says so, rather than blanking or silently claiming to be the new version.
    await waitFor(() => expect(notice(container, 'registry-updating')).not.toBeNull());
    expect(screen.getByText('Platform Lead')).toBeDefined();
    expect(screen.getByText(/Registry v7 · updating/)).toBeDefined();

    await act(async () => {
      release?.();
      await Promise.resolve();
    });

    await screen.findByText('Growth Lead');
    expect(screen.getByText(/Registry v8/)).toBeDefined();
    expect(screen.queryByText('Platform Lead')).toBeNull();
    expect(notice(container, 'registry-updating')).toBeNull();
  });

  it('resubscribes and refetches after the connection is restored', async () => {
    const container = await renderConsole();
    await waitFor(() => expect(channelOf(container)).toBe('live'));
    const before = server.organogramRequests().length;

    act(() => hubControl.connection().dropConnection());

    await waitFor(() => expect(channelOf(container)).toBe('reconnecting'));
    expect(notice(container, 'reconnecting')).not.toBeNull();
    // The organogram is not taken away while the transport is being restored.
    expect(screen.getByText('Head of Delivery')).toBeDefined();

    act(() => hubControl.connection().restoreConnection());

    await waitFor(() => expect(channelOf(container)).toBe('live'));
    expect(hubControl.connection().callsTo(SUBSCRIBE)).toEqual([ORGANIZATION_ID, ORGANIZATION_ID]);
    await waitFor(() => expect(server.organogramRequests().length).toBe(before + 1));
    expect(notice(container, 'reconnecting')).toBeNull();
  });
});

describe('ConsoleApp on the polling fallback', () => {
  beforeEach(() => {
    hubControl.startError = new Error('hub unreachable');
  });

  it('keeps the view current by polling when the hub will not start, and says so', async () => {
    const container = await renderConsole();

    await waitFor(() => expect(channelOf(container)).toBe('polling'));
    expect(within(container).getByText('Polling fallback')).toBeDefined();
    expect(notice(container, 'polling')?.textContent).toContain('polled every 5s');
    // A hub that cannot start is a degraded mode, not a failed view.
    expect(container.querySelector('.panel--error')).toBeNull();
    expect(screen.getByText('Head of Delivery')).toBeDefined();
  });

  it('applies polled states and then polls conditionally with the returned ETag', async () => {
    const container = await renderConsole();
    await waitFor(() => expect(channelOf(container)).toBe('polling'));

    server.positionStates = () =>
      Promise.resolve(
        statesResponse(
          [positionState({ positionId: 'runtime-lead', state: 'Working', sequence: 21 })],
          '"v7-1"',
        ),
      );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(CONFIG.pollIntervalMs);
    });

    await waitFor(() => expect(stateOf(container, 'runtime-lead')).toBe('Working'));
    expect(server.positionStateRequests()[0]?.headers['if-none-match']).toBeUndefined();

    server.positionStates = () => Promise.resolve(notModified('"v7-1"'));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(CONFIG.pollIntervalMs);
    });

    await waitFor(() => expect(server.positionStateRequests().length).toBeGreaterThan(1));
    const latest = server.positionStateRequests().at(-1);
    expect(latest?.headers['if-none-match']).toBe('"v7-1"');
    // An unchanged answer is still an agreement with the server.
    expect(stateOf(container, 'runtime-lead')).toBe('Working');
    expect(notice(container, 'update-failed')).toBeNull();
  });

  it('keeps the last organogram and admits the failure when an update stops working', async () => {
    const container = await renderConsole();
    await waitFor(() => expect(channelOf(container)).toBe('polling'));

    server.positionStates = () => Promise.reject(new TypeError('Failed to fetch'));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(CONFIG.pollIntervalMs);
    });

    await waitFor(() => expect(notice(container, 'update-failed')).not.toBeNull());
    expect(screen.getByText('Head of Delivery')).toBeDefined();
    expect(container.querySelector('.panel--error')).toBeNull();
  });

  it('warns that the view may be out of date once updates stop succeeding', async () => {
    const container = await renderConsole();
    await waitFor(() => expect(channelOf(container)).toBe('polling'));

    server.positionStates = () => Promise.reject(new TypeError('Failed to fetch'));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });

    await waitFor(() => expect(notice(container, 'stale')).not.toBeNull());
    expect(container.querySelector('.update-indicator')?.getAttribute('data-freshness')).toBe('stale');
  });
});

describe('ConsoleApp when there is no organogram to show', () => {
  it('distinguishes an empty organization from a failure to load one', async () => {
    server.organogram = () => Promise.resolve(jsonResponse(snapshot()));
    const { container } = render(<ConsoleApp config={CONFIG} />);

    await screen.findByText('This organization has no units or positions');
    expect(container.querySelector('.panel--error')).toBeNull();
    expect(container.querySelector('.filters')).toBeNull();
  });

  it('shows a retryable failure when the API cannot be reached, and recovers on retry', async () => {
    server.organogram = () => Promise.reject(new TypeError('Failed to fetch'));
    const { container } = render(<ConsoleApp config={CONFIG} />);

    await screen.findByText('The API could not be reached');
    expect(container.querySelector('[data-stage]')?.getAttribute('data-stage')).toBe('failed');

    server.organogram = () => Promise.resolve(jsonResponse(deliveryOrganization()));
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));

    await screen.findByText('Head of Delivery');
    expect(screen.queryByText('The API could not be reached')).toBeNull();
  });

  it('does not offer a retry for a credential the API rejected', async () => {
    server.organogram = () => Promise.resolve(problemResponse(401, 'Unauthorized'));
    render(<ConsoleApp config={CONFIG} />);

    await screen.findByText('The credential was rejected');
    expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull();
  });

  it('does not reveal whether an out-of-scope organization exists', async () => {
    server.organogram = () => Promise.resolve(problemResponse(404, 'Organization not found'));
    render(<ConsoleApp config={CONFIG} />);

    await screen.findByText('No organization is visible to this credential');
    expect(screen.getByText(/either unknown or outside the scope/)).toBeDefined();
  });

  it('treats a read model that is not materialized yet as retryable', async () => {
    server.organogram = () => Promise.resolve(problemResponse(503, 'Read model unavailable'));
    render(<ConsoleApp config={CONFIG} />);

    await screen.findByText('The organogram read model is not ready');
    expect(screen.getByRole('button', { name: 'Try again' })).toBeDefined();
  });
});

describe('ConsoleApp read-only guarantee', () => {
  it('exposes no control beyond re-reading and narrowing the view', async () => {
    const container = await renderConsole();
    // Scoped to the organogram section: the shell also carries navigation to the
    // inbox (US-F1-02), which is where a person acts. The guarantee this test
    // protects is that the organogram itself never becomes such a place.
    const organogram = container.querySelector('.console__section');
    expect(organogram).not.toBeNull();

    const allowed = ['Clear filters', 'Refresh now', 'Try again'];
    for (const button of organogram!.querySelectorAll('button')) {
      expect(allowed).toContain(button.textContent);
    }

    expect(organogram!.querySelector('form')).toBeNull();
    expect(organogram!.querySelector('textarea')).toBeNull();
    expect(organogram!.querySelector('[contenteditable]')).toBeNull();
  });

  it('navigates between sections without offering any other shell control', async () => {
    const container = await renderConsole();
    const nav = container.querySelector('.console__nav');
    expect(nav).not.toBeNull();

    expect([...nav!.querySelectorAll('button')].map((button) => button.textContent)).toEqual([
      'Organogram',
      'Inbox',
    ]);
  });

  it('never sends anything other than reads to the API', async () => {
    const container = await renderConsole();
    await waitFor(() => expect(channelOf(container)).toBe('live'));

    act(() => {
      hubControl.connection().emit(HUB_EVENTS.positionStateChanged, {
        organization_id: ORGANIZATION_ID,
        state: positionState({ positionId: 'runtime-lead', state: 'Working', sequence: 30 }),
      });
    });
    await waitFor(() => expect(stateOf(container, 'runtime-lead')).toBe('Working'));

    for (const [, init] of fetchMock.mock.calls as [string, RequestInit | undefined][]) {
      expect(init?.method ?? 'GET').toBe('GET');
    }

    // The hub is only ever asked to subscribe or unsubscribe.
    for (const invocation of hubControl.connection().invocations) {
      expect(['SubscribeToOrganization', 'UnsubscribeFromOrganization']).toContain(invocation.method);
    }
  });
});
