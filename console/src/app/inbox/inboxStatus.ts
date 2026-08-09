/**
 * What the inbox must say about itself.
 *
 * The transport rules are the organogram's, reused rather than restated: live
 * only with an established subscription, delayed while polling, stale once
 * updates have stopped succeeding. Two situations are the inbox's own, and both
 * exist because it is the one view where the reader may be mid-task:
 *
 * - a committed change is known but withheld, because rebuilding a list someone
 *   has paged through would move what they were reading;
 * - notifications were missed, so the next snapshot is a recovery rather than an
 *   increment — worth saying, since a silent gap and a quiet inbox look alike.
 */

import type { UpdateChannel } from '../organogram/useOrganogramLiveView.js';
import type { ConsoleFailure, ConsoleFreshness, ConsoleNotice } from '../status/consoleStatus.js';
import {
  deriveFreshness,
  describeFailure,
  formatAge,
  formatSeconds,
} from '../status/consoleStatus.js';

export type InboxStage = 'loading' | 'failed' | 'empty' | 'ready';

export interface InboxStatus {
  readonly stage: InboxStage;
  readonly failure: ConsoleFailure | null;
  readonly freshness: ConsoleFreshness;
  readonly notices: readonly ConsoleNotice[];
}

export interface InboxStatusInput {
  readonly phase: 'loading' | 'ready' | 'failed';
  readonly error: Error | null;
  readonly loaded: boolean;
  readonly itemCount: number;
  readonly channel: UpdateChannel;
  readonly lastSyncedAtUtc: string | null;
  readonly projectionAppliedAtUtc: string | null;
  readonly pendingUpdate: boolean;
  readonly missedNotifications: boolean;
  readonly pollIntervalMs: number;
  /** Current instant in epoch milliseconds, injected so this stays pure. */
  readonly nowMs: number;
}

export function deriveInboxStatus(input: InboxStatusInput): InboxStatus {
  const freshness = deriveFreshness(
    input.channel,
    input.lastSyncedAtUtc,
    input.pollIntervalMs,
    input.nowMs,
  );
  const stage = deriveStage(input);

  return {
    stage,
    failure: stage === 'failed' ? describeFailure(input.error, 'inbox') : null,
    freshness,
    notices: stage === 'failed' || stage === 'loading' ? [] : deriveNotices(input, freshness),
  };
}

function deriveStage(input: InboxStatusInput): InboxStage {
  if (input.loaded) {
    // An answered inbox always wins: a later failure degrades the view, it does
    // not replace the items already on screen with an error page.
    return input.itemCount === 0 ? 'empty' : 'ready';
  }

  return input.phase === 'failed' ? 'failed' : 'loading';
}

function deriveNotices(
  input: InboxStatusInput,
  freshness: ConsoleFreshness,
): readonly ConsoleNotice[] {
  const notices: ConsoleNotice[] = [];

  if (input.channel === 'connecting') {
    notices.push({
      id: 'connecting',
      severity: 'info',
      message: 'Connecting to live updates. The inbox is being kept current by polling meanwhile.',
      retryable: false,
    });
  }

  if (input.channel === 'reconnecting') {
    notices.push({
      id: 'reconnecting',
      severity: 'warning',
      message:
        'The live connection dropped and is being re-established. Polling is covering the gap, and the inbox is refetched once the subscription returns.',
      retryable: false,
    });
  }

  if (input.channel === 'polling') {
    notices.push({
      id: 'polling',
      severity: 'warning',
      message: `Live updates are unavailable. The inbox is polled every ${formatSeconds(
        input.pollIntervalMs,
      )}, so new items and decisions appear with a delay.`,
      retryable: true,
    });
  }

  if (input.pendingUpdate) {
    notices.push({
      id: 'inbox-pending-update',
      severity: 'info',
      message:
        'The inbox changed while you were reading a later page. Refresh to rebuild the list from the first page.',
      retryable: true,
    });
  }

  if (input.missedNotifications) {
    notices.push({
      id: 'inbox-missed-notifications',
      severity: 'warning',
      message:
        'Some update notifications were missed. The list was refetched in full, so what is shown is current even though the gap cannot be reconstructed.',
      retryable: false,
    });
  }

  if (
    input.projectionAppliedAtUtc === null ||
    Number.isNaN(Date.parse(input.projectionAppliedAtUtc))
  ) {
    notices.push({
      id: 'projection-not-started',
      severity: 'warning',
      message:
        'The inbox projection has not reported an applied event. An empty result may be incomplete.',
      retryable: true,
    });
  }

  if (freshness.level === 'stale') {
    notices.push({
      id: 'stale',
      severity: 'warning',
      message: `No update has succeeded for ${formatAge(
        freshness.ageMs ?? 0,
      )}. New items or decisions may not be listed.`,
      retryable: true,
    });
  }

  if (input.error !== null) {
    const failure = describeFailure(input.error, 'inbox');
    notices.push({
      id: 'update-failed',
      severity: 'warning',
      message: `The last update attempt failed. ${failure.detail}`,
      retryable: failure.retryable,
    });
  }

  return notices;
}
