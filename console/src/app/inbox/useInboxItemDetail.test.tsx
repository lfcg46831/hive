// @vitest-environment jsdom

import { StrictMode } from 'react';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConsoleConfig } from '../../config.js';
import {
  approvalRequest,
  createInboxServer,
  inboxItem,
  inboxItemResponse,
  jsonResponse,
  problemResponse,
} from '../testing/inboxFixture.js';
import type { InboxServer } from '../testing/inboxFixture.js';
import { useInboxItemDetail } from './useInboxItemDetail.js';
import type { InboxActionKind, InboxItemDetailView } from './useInboxItemDetail.js';

const CONFIG: ConsoleConfig = {
  apiBaseUrl: 'https://hive.example.com',
  organizationId: 'acme-delivery',
  token: 'person-token',
  pollIntervalMs: 3_600_000,
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => { resolve = complete; });
  return { promise, resolve };
}

let server: InboxServer;
beforeEach(() => {
  server = createInboxServer([
    approvalRequest({ id: 'A', canDecide: true }),
    inboxItem({ id: 'B' }),
    inboxItem({ id: 'C' }),
  ]);
  server.drafts.set('B', 'B draft');
  vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => server.fetch(url, init)));
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

async function setup() {
  const onCommitted = vi.fn();
  const hook = renderHook(
    ({ id, config }: { id: string | null; config: ConsoleConfig }) =>
      useInboxItemDetail(config, id, onCommitted),
    { initialProps: { id: 'A' as string | null, config: CONFIG }, wrapper: StrictMode },
  );
  await waitFor(() => expect(hook.result.current.phase).toBe('ready'));
  return { ...hook, onCommitted };
}

function startAction(view: InboxItemDetailView, kind: InboxActionKind) {
  switch (kind) {
    case 'read': return view.setRead(true);
    case 'draft': return view.saveDraft('A draft');
    case 'reply': return view.reply('A reply', 'done');
    case 'decision': return view.decide(true, 'A reason');
  }
}

describe('inbox detail selection isolation (BUG-022)', () => {
  it('blocks every action until the newly selected snapshot is loaded, including retained callbacks', async () => {
    const { result, rerender } = await setup();
    const previous = result.current;
    const pending = deferred<Response>();
    const answer = server.detail;
    server.detail = (request) => request.path.endsWith('/B') ? pending.promise : answer(request);

    rerender({ id: 'B', config: CONFIG });
    expect(result.current.phase).toBe('loading');
    expect(result.current.item).toBeNull();
    expect(result.current.content).toBeNull();
    expect(result.current.draftText).toBeNull();
    expect(result.current.lastEventAppliedAtUtc).toBeNull();
    await act(async () => {
      for (const view of [previous, result.current]) {
        for (const kind of ['read', 'draft', 'reply', 'decision'] as const) {
          await startAction(view, kind);
        }
      }
    });
    expect(server.requests.filter((request) => request.method === 'POST')).toHaveLength(0);

    await act(async () => pending.resolve(jsonResponse(inboxItemResponse(server.find('B')!))));
    await waitFor(() => expect(result.current.item?.item_id).toBe('B'));
    act(() => result.current.setRead(true));
    await waitFor(() => expect(result.current.item?.read_state).toBe('Read'));
    expect(server.requestsTo('/B/read')).toHaveLength(1);
  });

  it.each(['read', 'draft', 'reply', 'decision'] as const)(
    'ignores a late %s success without clearing the next item’s in-flight action',
    async (kind) => {
      server.items[0] = kind === 'decision'
        ? approvalRequest({ id: 'A', canDecide: true }) : inboxItem({ id: 'A' });
      const { result, rerender, onCommitted } = await setup();
      const pending = deferred<Response>();
      const route = kind === 'decision' ? 'decide' : kind;
      const answer = server[route];
      server[route] = async (request) => {
        const response = await answer(request);
        await pending.promise;
        return response;
      };
      let completion: Promise<boolean> | void;
      act(() => { completion = startAction(result.current, kind); });
      await waitFor(() => expect(result.current.busy).toBe(kind));

      rerender({ id: 'B', config: CONFIG });
      await waitFor(() => expect(result.current.item?.item_id).toBe('B'));
      expect(result.current.busy).toBeNull();
      const next = deferred<Response>();
      server.draft = () => next.promise;
      act(() => result.current.saveDraft('B edited draft'));
      const before = result.current;
      const fetches = server.requestsTo('/inbox/B').length;

      await act(async () => { pending.resolve(jsonResponse({})); await completion; });
      expect(result.current.item).toEqual(before.item);
      expect(result.current.content).toEqual(before.content);
      expect(result.current.draftText).toBe('B draft');
      expect(result.current.lastEventAppliedAtUtc).toBe(before.lastEventAppliedAtUtc);
      expect(result.current.busy).toBe('draft');
      expect(result.current.outcome).toBeNull();
      expect(result.current.actionError).toBeNull();
      expect(onCommitted).not.toHaveBeenCalled();
      expect(server.requestsTo('/inbox/B')).toHaveLength(fetches);
      expect(server.requestsTo(`/A/${kind}`)).toHaveLength(1);
      const postCalls = vi.mocked(fetch).mock.calls.filter(([, init]) => init?.method === 'POST');
      expect(postCalls[0]?.[1]?.signal?.aborted ?? false).toBe(false);
    },
  );

  it.each(['read', 'draft', 'reply', 'decision'] as const)(
    'ignores a late %s rejection after changing selection',
    async (kind) => {
      const { result, rerender } = await setup();
      const pending = deferred<Response>();
      server[kind === 'decision' ? 'decide' : kind] = () => pending.promise;
      let completion: Promise<boolean> | void;
      act(() => { completion = startAction(result.current, kind); });
      rerender({ id: 'B', config: CONFIG });
      await waitFor(() => expect(result.current.item?.item_id).toBe('B'));
      await act(async () => {
        pending.resolve(problemResponse(422, 'A rejected'));
        await completion;
      });
      expect(result.current.actionError).toBeNull();
      expect(result.current.busy).toBeNull();
      expect(result.current.outcome).toBeNull();
    },
  );

  it.each(['A', 'C'])('keeps the newest snapshot after A → B → %s and late fetches', async (lastId) => {
    const { result, rerender } = await setup();
    const pendingA = deferred<Response>();
    const pendingB = deferred<Response>();
    server.detail = (request) => request.path.endsWith('/B') ? pendingB.promise : pendingA.promise;
    act(() => result.current.reload());
    rerender({ id: 'B', config: CONFIG });
    const selected = server.find(lastId)!;
    server.detail = () => jsonResponse(inboxItemResponse(selected, 'Newest draft'));
    rerender({ id: lastId, config: CONFIG });
    await waitFor(() => expect(result.current.draftText).toBe('Newest draft'));
    await act(async () => {
      pendingB.resolve(problemResponse(404, 'Old B fetch failed'));
      pendingA.resolve(jsonResponse(inboxItemResponse(server.find('A')!, 'Old A draft')));
    });
    expect(result.current.item?.item_id).toBe(lastId);
    expect(result.current.draftText).toBe('Newest draft');
    expect(result.current.phase).toBe('ready');
    expect(result.current.error).toBeNull();
  });

  it('does not apply an old mutation when the same item is selected again', async () => {
    const { result, rerender } = await setup();
    const pending = deferred<Response>();
    const answer = server.draft;
    server.draft = async (request) => {
      const response = await answer(request);
      await pending.promise;
      return response;
    };
    act(() => result.current.saveDraft('Old draft'));
    await waitFor(() => expect(server.drafts.get('A')).toBe('Old draft'));
    rerender({ id: 'B', config: CONFIG });
    server.drafts.set('A', 'Newest draft');
    rerender({ id: 'A', config: CONFIG });
    await waitFor(() => expect(result.current.draftText).toBe('Newest draft'));
    await act(async () => pending.resolve(jsonResponse({})));
    expect(result.current.draftText).toBe('Newest draft');
  });

  it.each([
    { id: null, config: CONFIG },
    { id: 'A', config: { ...CONFIG, organizationId: 'another-org' } },
    { id: 'A', config: { ...CONFIG, token: 'another-person' } },
  ])('invalidates actions when the selection or identity changes: %j', async (props) => {
    const { result, rerender, onCommitted } = await setup();
    const pending = deferred<Response>();
    server.decide = () => pending.promise;
    let completion: Promise<boolean>;
    act(() => { completion = result.current.decide(true, null); });
    const previous = result.current;
    server.detail = () => new Promise<Response>(() => {});
    rerender(props);
    expect(result.current.item).toBeNull();
    expect(result.current.busy).toBeNull();
    await act(async () => {
      previous.setRead(true);
      pending.resolve(jsonResponse({}));
      await completion;
    });
    expect(result.current.outcome).toBeNull();
    expect(onCommitted).not.toHaveBeenCalled();
    expect(server.requestsTo('/read')).toHaveLength(0);
  });
});
