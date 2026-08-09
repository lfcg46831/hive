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

import { useCallback, useEffect, useMemo, useState } from 'react';
import type {
  InboxDecisionResponse,
  InboxItem,
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
  /** The single plain-text draft this principal holds for the item. */
  readonly draftText: string | null;
  readonly lastEventAppliedAtUtc: string | null;
  /** The action currently in flight, so the view can disable exactly that one. */
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

export function useInboxItemDetail(
  config: ConsoleConfig,
  itemId: string | null,
  onCommitted: () => void,
): InboxItemDetailView {
  const client = useMemo(
    () => createHiveApiClient({ baseUrl: config.apiBaseUrl, token: config.token }),
    [config.apiBaseUrl, config.token],
  );

  const [phase, setPhase] = useState<'idle' | 'loading' | 'ready' | 'failed'>('idle');
  const [error, setError] = useState<Error | null>(null);
  const [item, setItem] = useState<InboxItem | null>(null);
  const [draftText, setDraftText] = useState<string | null>(null);
  const [lastEventAppliedAtUtc, setLastEventAppliedAtUtc] = useState<string | null>(null);
  const [busy, setBusy] = useState<InboxActionKind | null>(null);
  const [actionError, setActionError] = useState<Error | null>(null);
  const [outcome, setOutcome] = useState<InboxEmissionOutcome | null>(null);
  const [reloadToken, setReloadToken] = useState(0);

  const reload = useCallback(() => setReloadToken((token) => token + 1), []);

  useEffect(() => {
    if (itemId === null) {
      setPhase('idle');
      setItem(null);
      setDraftText(null);
      setOutcome(null);
      setActionError(null);
      setError(null);
      return undefined;
    }

    const abort = new AbortController();
    let cancelled = false;
    setPhase((current) => (current === 'ready' ? current : 'loading'));

    void (async () => {
      try {
        const result = await client.getInboxItem(config.organizationId, itemId, {
          signal: abort.signal,
        });
        if (cancelled || result.status === 'not-modified') {
          return;
        }

        setItem(result.snapshot.item);
        setDraftText(result.snapshot.draft_text);
        setLastEventAppliedAtUtc(result.snapshot.last_event_applied_at_utc);
        setError(null);
        setPhase('ready');
      } catch (cause) {
        if (cancelled || abort.signal.aborted) {
          return;
        }

        setError(toError(cause));
        setPhase('failed');
      }
    })();

    return () => {
      cancelled = true;
      abort.abort();
    };
  }, [client, config.organizationId, itemId, reloadToken]);

  // Selecting another item must not carry the previous item's outcome with it.
  useEffect(() => {
    setOutcome(null);
    setActionError(null);
  }, [itemId]);

  const run = useCallback(
    async <T>(kind: InboxActionKind, action: (id: string) => Promise<T>): Promise<T | null> => {
      if (itemId === null) {
        return null;
      }

      setBusy(kind);
      setActionError(null);
      try {
        return await action(itemId);
      } catch (cause) {
        setActionError(toError(cause));
        return null;
      } finally {
        setBusy(null);
      }
    },
    [itemId],
  );

  const setRead = useCallback(
    (read: boolean) => {
      void run('read', async (id) => {
        const state = await client.setInboxItemRead(config.organizationId, id, read);
        setItem((current) =>
          current === null ? current : { ...current, read_state: state.read_state },
        );
        setLastEventAppliedAtUtc(state.last_event_applied_at_utc);
        onCommitted();
      });
    },
    [client, config.organizationId, onCommitted, run],
  );

  const saveDraft = useCallback(
    (body: string | null) => {
      void run('draft', async (id) => {
        const state = await client.saveInboxItemDraft(config.organizationId, id, body);
        setDraftText(state.draft_text);
        setItem((current) =>
          current === null ? current : { ...current, response_state: state.response_state },
        );
        setLastEventAppliedAtUtc(state.last_event_applied_at_utc);
        onCommitted();
      });
    },
    [client, config.organizationId, onCommitted, run],
  );

  const reply = useCallback(
    async (body: string, reportKind: string | null): Promise<boolean> => {
      const response = await run('reply', (id) =>
        client.replyToInboxItem(config.organizationId, id, {
          body,
          ...(reportKind === null ? {} : { report_kind: reportKind }),
        }),
      );
      if (response === null) {
        return false;
      }

      setOutcome({ kind: 'reply', response });
      onCommitted();
      reload();
      return true;
    },
    [client, config.organizationId, onCommitted, reload, run],
  );

  const decide = useCallback(
    async (approved: boolean, reason: string | null): Promise<boolean> => {
      const response = await run('decision', (id) =>
        client.decideInboxApproval(config.organizationId, id, {
          approved,
          ...(reason === null ? {} : { reason }),
        }),
      );
      if (response === null) {
        return false;
      }

      setOutcome({ kind: 'decision', response });
      onCommitted();
      reload();
      return true;
    },
    [client, config.organizationId, onCommitted, reload, run],
  );

  return {
    phase,
    error,
    item,
    draftText,
    lastEventAppliedAtUtc,
    busy,
    actionError,
    outcome,
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
