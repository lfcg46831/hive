/**
 * Data layer of the selected inbox item and of the actions on it.
 *
 * The two kinds of action are kept apart on purpose, because they differ in what
 * they mean. Read state and drafts are interface facts: they are persisted per
 * person, they emit nothing, and they are answered synchronously. A reply or a
 * decision is a request to the occupied position to emit a canonical message; it
 * is answered `202 Accepted`, so what comes back is the metadata of the emitted
 * message, not the new state of the inbox. The view reports the emission and
 * refetches — it never edits the derived state itself.
 */

import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type {
  InboxDecisionResponse,
  InboxItem,
  InboxMessageContent,
  InboxReplyResponse,
} from '../../api/index.js';
import { createHiveApiClient } from '../../api/index.js';
import type { ConsoleConfig } from '../../config.js';

export type InboxActionKind = 'read' | 'draft' | 'reply' | 'decision';

export type InboxEmissionOutcome =
  | { readonly kind: 'reply'; readonly response: InboxReplyResponse }
  | { readonly kind: 'decision'; readonly response: InboxDecisionResponse };

export interface InboxItemDetailView {
  readonly phase: 'idle' | 'loading' | 'ready' | 'failed';
  readonly error: Error | null;
  readonly item: InboxItem | null;
  /**
   * Canonical content of the message, which only the detail route carries. Null
   * means the projection holds the item without it — never an empty message.
   */
  readonly content: InboxMessageContent | null;
  /** The single plain-text draft this principal holds for the item. */
  readonly draftText: string | null;
  readonly lastEventAppliedAtUtc: string | null;
  /** The action currently in flight for this selection; blocks other actions. */
  readonly busy: InboxActionKind | null;
  /** Rejection of the last attempted action, kept until the next attempt. */
  readonly actionError: Error | null;
  /** Metadata of the last message the occupied position emitted. */
  readonly outcome: InboxEmissionOutcome | null;
  setRead(read: boolean): void;
  saveDraft(body: string | null): void;
  reply(body: string, reportKind: string | null): Promise<boolean>;
  decide(approved: boolean, reason: string | null): Promise<boolean>;
  reload(): void;
}

type DetailState = Pick<InboxItemDetailView,
  'phase' | 'error' | 'item' | 'content' | 'draftText' | 'lastEventAppliedAtUtc' |
  'busy' | 'actionError' | 'outcome'
>;

function emptyDetail(itemId: string | null): DetailState {
  return {
    phase: itemId === null ? 'idle' : 'loading',
    error: null,
    item: null,
    content: null,
    draftText: null,
    lastEventAppliedAtUtc: null,
    busy: null,
    actionError: null,
    outcome: null,
  };
}

export function useInboxItemDetail(
  config: ConsoleConfig,
  itemId: string | null,
  onCommitted: () => void,
): InboxItemDetailView {
  const client = useMemo(
    () => createHiveApiClient({ baseUrl: config.apiBaseUrl, token: config.token }),
    [config.apiBaseUrl, config.token],
  );

  // Object identity distinguishes separate visits to the same item, including
  // A → B → A. A credential, organization or API change also starts a new visit.
  const selection = useMemo(
    () => ({ itemId, organizationId: config.organizationId, client }),
    [itemId, config.organizationId, client],
  );
  const [state, setState] = useState(() => ({ selection, ...emptyDetail(itemId) }));
  const [reloadToken, setReloadToken] = useState(0);
  const active = useRef<typeof state | null>(null);
  const pending = useRef<typeof selection | null>(null);

  // Reset before rendering children, so no frame pairs A's detail with B's
  // callbacks. Resetting in a passive effect would leave that window open.
  if (state.selection !== selection) {
    setState({ selection, ...emptyDetail(itemId) });
  }

  useLayoutEffect(() => {
    active.current = state;
    return () => { active.current = null; };
  }, [state]);

  const update = useCallback((change: (current: DetailState) => DetailState) => {
    setState((current) => current.selection === selection
      ? { selection, ...change(current) }
      : current);
  }, [selection]);

  const reload = useCallback(() => {
    if (active.current?.selection === selection) {
      setReloadToken((token) => token + 1);
    }
  }, [selection]);

  useEffect(() => {
    if (itemId === null) {
      return undefined;
    }

    const abort = new AbortController();
    let cancelled = false;
    update((current) => ({
      ...current,
      phase: current.phase === 'ready' ? 'ready' : 'loading',
    }));

    void (async () => {
      try {
        const result = await client.getInboxItem(config.organizationId, itemId, {
          signal: abort.signal,
        });
        if (cancelled || active.current?.selection !== selection || result.status === 'not-modified') {
          return;
        }

        if (result.snapshot.item.item_id !== itemId) {
          throw new Error('The API returned a different inbox item.');
        }
        // Item and content always come from the same snapshot, so the panel can
        // never show one message's text next to another message's metadata.
        update((current) => ({
          ...current,
          item: result.snapshot.item,
          content: result.snapshot.content ?? null,
          draftText: result.snapshot.draft_text,
          lastEventAppliedAtUtc: result.snapshot.last_event_applied_at_utc,
          error: null,
          phase: 'ready',
        }));
      } catch (cause) {
        if (cancelled || abort.signal.aborted || active.current?.selection !== selection) {
          return;
        }

        update((current) => ({ ...current, error: toError(cause), phase: 'failed' }));
      }
    })();

    return () => {
      cancelled = true;
      abort.abort();
    };
  }, [client, config.organizationId, itemId, reloadToken, selection, update]);

  const run = useCallback(
    async <T>(
      kind: InboxActionKind,
      action: (id: string) => Promise<T>,
      apply: (current: DetailState, response: T) => DetailState,
    ): Promise<boolean> => {
      const current = active.current;
      if (current?.selection !== selection || current.phase !== 'ready' ||
          current.item === null || current.item.item_id !== itemId ||
          pending.current === selection) {
        return false;
      }

      pending.current = selection;
      update((value) => ({ ...value, busy: kind, actionError: null }));
      try {
        // Submitted mutations are never aborted or retried on selection change.
        // Only applying their completion to the current view is conditional.
        const response = await action(current.item.item_id);
        if (active.current?.selection !== selection) {
          return false;
        }
        update((value) => apply(value, response));
        onCommitted();
        if (kind === 'reply' || kind === 'decision') {
          reload();
        }
        return true;
      } catch (cause) {
        if (active.current?.selection === selection) {
          update((value) => ({ ...value, actionError: toError(cause) }));
        }
        return false;
      } finally {
        if (pending.current === selection) {
          pending.current = null;
        }
        if (active.current?.selection === selection) {
          update((value) => ({ ...value, busy: null }));
        }
      }
    },
    [itemId, selection, onCommitted, reload, update],
  );

  const setRead = useCallback(
    (read: boolean) => {
      void run('read',
        (id) => client.setInboxItemRead(config.organizationId, id, read),
        (current, response) => ({
          ...current,
          item: current.item === null ? null : { ...current.item, read_state: response.read_state },
          lastEventAppliedAtUtc: response.last_event_applied_at_utc,
        }),
      );
    },
    [client, config.organizationId, run],
  );

  const saveDraft = useCallback(
    (body: string | null) => {
      void run('draft',
        (id) => client.saveInboxItemDraft(config.organizationId, id, body),
        (current, response) => ({
          ...current,
          draftText: response.draft_text,
          item: current.item === null ? null : { ...current.item, response_state: response.response_state },
          lastEventAppliedAtUtc: response.last_event_applied_at_utc,
        }),
      );
    },
    [client, config.organizationId, run],
  );

  const reply = useCallback(
    async (body: string, reportKind: string | null): Promise<boolean> => {
      return run('reply', (id) =>
        client.replyToInboxItem(config.organizationId, id, {
          body,
          ...(reportKind === null ? {} : { report_kind: reportKind }),
        }),
        (current, response) => ({ ...current, outcome: { kind: 'reply', response } }),
      );
    },
    [client, config.organizationId, run],
  );

  const decide = useCallback(
    async (approved: boolean, reason: string | null): Promise<boolean> => {
      return run('decision', (id) =>
        client.decideInboxApproval(config.organizationId, id, {
          approved,
          ...(reason === null ? {} : { reason }),
        }),
        (current, response) => ({ ...current, outcome: { kind: 'decision', response } }),
      );
    },
    [client, config.organizationId, run],
  );

  const detail = state.selection === selection ? state : emptyDetail(itemId);
  return {
    phase: detail.phase,
    error: detail.error,
    item: detail.item,
    content: detail.content,
    draftText: detail.draftText,
    lastEventAppliedAtUtc: detail.lastEventAppliedAtUtc,
    busy: detail.busy,
    actionError: detail.actionError,
    outcome: detail.outcome,
    setRead,
    saveDraft,
    reply,
    decide,
    reload,
  };
}

function toError(cause: unknown): Error {
  return cause instanceof Error ? cause : new Error(String(cause));
}
