/**
 * Derivation of what the console must say about itself.
 *
 * The organogram is only trustworthy when the view is explicit about how it was
 * obtained: realtime is an optimization (US-F1-01-T08), so a reader has to be
 * able to tell a live organogram from one that is a poll interval behind, from
 * one that has not been refreshed at all because the last attempt failed. This
 * module owns that judgement as a pure function of the live-view state and the
 * current instant, so the presentation layer renders it and the rules stay
 * testable without a DOM.
 *
 * Nothing here re-derives organizational facts: operational state, precedence
 * and registry version come from the API. What is derived here is the state of
 * the *transport*, plus the two shapes a snapshot can take that are not an
 * organogram — nothing yet, and nothing at all.
 */

import { HiveApiError } from '../../api/index.js';
import type { OrganogramResponse } from '../../api/index.js';
import type { UpdateChannel } from '../organogram/useOrganogramLiveView.js';

/** What the console renders as its main region. */
export type ConsoleStage = 'loading' | 'failed' | 'empty' | 'ready';

/**
 * How much the displayed data can be trusted to be current.
 *
 * `live` is claimed only with an established subscription, because a hub that is
 * connected would have delivered a state change. In every other channel the
 * view is at best one poll interval behind (`delayed`), and once successful
 * updates stop arriving for several intervals it is `stale` — the interesting
 * case, since a silent organization and a broken poll look identical on screen.
 */
export type FreshnessLevel = 'live' | 'delayed' | 'stale' | 'unknown';

export type NoticeSeverity = 'info' | 'warning';

export interface ConsoleNotice {
  /** Stable key for rendering and for tests to assert on. */
  readonly id:
    | 'connecting'
    | 'reconnecting'
    | 'polling'
    | 'registry-updating'
    | 'stale'
    | 'projection-not-started'
    | 'update-failed'
    | 'inbox-pending-update'
    | 'inbox-missed-notifications';
  readonly severity: NoticeSeverity;
  readonly message: string;
  /** True when a manual snapshot refetch is a sensible reaction. */
  readonly retryable: boolean;
}

export interface ConsoleFailure {
  readonly title: string;
  readonly detail: string;
  /** False for failures a retry cannot fix, such as a rejected credential. */
  readonly retryable: boolean;
}

export interface ConsoleFreshness {
  readonly level: FreshnessLevel;
  readonly label: string;
  /** Age of the last agreement with the server, or null when there is none. */
  readonly ageMs: number | null;
}

export interface ConsoleStatus {
  readonly stage: ConsoleStage;
  /** Present exactly when `stage` is `failed`. */
  readonly failure: ConsoleFailure | null;
  readonly freshness: ConsoleFreshness;
  readonly notices: readonly ConsoleNotice[];
  /** True while a snapshot refetch is in flight over an already shown view. */
  readonly refreshing: boolean;
}

export interface ConsoleStatusInput {
  readonly phase: 'loading' | 'ready' | 'failed';
  readonly error: Error | null;
  readonly snapshot: OrganogramResponse | null;
  readonly channel: UpdateChannel;
  readonly lastSyncedAtUtc: string | null;
  readonly registryUpdating: boolean;
  readonly refreshing: boolean;
  readonly pollIntervalMs: number;
  /** Current instant in epoch milliseconds, injected so this stays pure. */
  readonly nowMs: number;
}

/**
 * Multiple of the poll interval after which the view stops calling itself
 * merely delayed. Three intervals means two consecutive failed or dropped polls
 * before the reader is warned, which keeps a single slow response quiet.
 */
const STALE_POLL_INTERVALS = 3;

/** Floor for the staleness threshold, so a fast poll interval is not alarmist. */
const MIN_STALE_AFTER_MS = 15_000;

export function deriveConsoleStatus(input: ConsoleStatusInput): ConsoleStatus {
  const freshness = deriveFreshness(
    input.channel,
    input.lastSyncedAtUtc,
    input.pollIntervalMs,
    input.nowMs,
  );
  const stage = deriveStage(input);

  return {
    stage,
    failure: stage === 'failed' ? describeFailure(input.error) : null,
    freshness,
    notices: stage === 'failed' || stage === 'loading' ? [] : deriveNotices(input, freshness),
    refreshing: input.refreshing,
  };
}

/**
 * A snapshot that holds neither units nor positions. Distinguished from a failed
 * load because the two demand opposite reactions: an empty organization is a
 * registry to fix, an unreachable API is a deployment to fix.
 */
export function isEmptySnapshot(snapshot: OrganogramResponse): boolean {
  return snapshot.units.length === 0 && snapshot.positions.length === 0;
}

/** Which view is reporting the failure, so the wording names the right thing. */
export type FailureSubject = 'organogram' | 'inbox';

/** Turns an API failure into something a reader can act on. */
export function describeFailure(
  error: Error | null,
  subject: FailureSubject = 'organogram',
): ConsoleFailure {
  if (error === null) {
    return {
      title: `The ${subject} could not be loaded`,
      detail: 'The API did not return a snapshot.',
      retryable: true,
    };
  }

  if (error instanceof HiveApiError) {
    if (error.isUnauthorized) {
      return {
        title: 'The credential was rejected',
        detail:
          'The configured token is unknown to the API. Check the console configuration before retrying.',
        retryable: false,
      };
    }

    if (error.isNotFound) {
      // The API answers alike for absent and out-of-scope organizations, so the
      // console must not imply which of the two happened. The inbox adds a third
      // cause that is not a disclosure at all — a credential with no person bound
      // to it — and naming it is the difference between an operator fixing their
      // configuration and hunting a registry problem that does not exist.
      return subject === 'inbox'
        ? {
            title: 'No inbox is visible to this credential',
            detail:
              'An inbox needs both an organization in scope and a person bound to the credential. Either the organization is unknown or outside this credential’s scope, or the credential has no person binding — if the organogram loads with the same token, the person binding is what is missing.',
            retryable: false,
          }
        : {
            title: 'No organization is visible to this credential',
            detail:
              'The configured organization is either unknown or outside the scope of this credential.',
            retryable: false,
          };
    }

    if (error.isReadModelUnavailable) {
      return subject === 'inbox'
        ? {
            title: 'The inbox read model is not ready',
            detail:
              'The projection has not materialized yet. This resolves on its own once it catches up.',
            retryable: true,
          }
        : {
            title: 'The organogram read model is not ready',
            detail:
              'The registry snapshot has not been materialized yet. This resolves on its own once the import completes.',
            retryable: true,
          };
    }

    return {
      title: 'The API rejected the request',
      detail: error.problem?.detail ?? error.message,
      retryable: error.status >= 500,
    };
  }

  return {
    title: 'The API could not be reached',
    detail: error.message,
    retryable: true,
  };
}

function deriveStage(input: ConsoleStatusInput): ConsoleStage {
  if (input.snapshot !== null) {
    // A snapshot in hand always wins: a later failure degrades the view, it
    // does not replace what is known with an error page.
    return isEmptySnapshot(input.snapshot) ? 'empty' : 'ready';
  }

  return input.phase === 'failed' ? 'failed' : 'loading';
}

/**
 * How current the displayed data is, given how it is being kept up to date.
 * Shared by every view over the same transport, so «live» means the same thing
 * in the organogram and in the inbox.
 */
export function deriveFreshness(
  channel: UpdateChannel,
  lastSyncedAtUtc: string | null,
  pollIntervalMs: number,
  nowMs: number,
): ConsoleFreshness {
  if (lastSyncedAtUtc === null) {
    return { level: 'unknown', label: 'Freshness unknown', ageMs: null };
  }

  const syncedAtMs = Date.parse(lastSyncedAtUtc);
  if (Number.isNaN(syncedAtMs)) {
    return { level: 'unknown', label: 'Freshness unknown', ageMs: null };
  }

  const ageMs = Math.max(0, nowMs - syncedAtMs);
  if (channel === 'live') {
    // An established subscription would have delivered a change, so silence is
    // evidence of an unchanged organization rather than of a stalled view.
    return { level: 'live', label: 'Up to date', ageMs };
  }

  const staleAfterMs = Math.max(MIN_STALE_AFTER_MS, pollIntervalMs * STALE_POLL_INTERVALS);
  return ageMs > staleAfterMs
    ? { level: 'stale', label: `Possibly out of date · ${formatAge(ageMs)} without an update`, ageMs }
    : { level: 'delayed', label: `Updated ${formatAge(ageMs)} ago`, ageMs };
}

function deriveNotices(
  input: ConsoleStatusInput,
  freshness: ConsoleFreshness,
): readonly ConsoleNotice[] {
  const notices: ConsoleNotice[] = [];

  if (input.channel === 'connecting') {
    notices.push({
      id: 'connecting',
      severity: 'info',
      message: 'Connecting to live updates. The view is being kept current by polling meanwhile.',
      retryable: false,
    });
  }

  if (input.channel === 'reconnecting') {
    notices.push({
      id: 'reconnecting',
      severity: 'warning',
      message:
        'The live connection dropped and is being re-established. Polling is covering the gap, and the snapshot is refetched once the subscription returns.',
      retryable: false,
    });
  }

  if (input.channel === 'polling') {
    notices.push({
      id: 'polling',
      severity: 'warning',
      message: `Live updates are unavailable. States are polled every ${formatSeconds(
        input.pollIntervalMs,
      )}, so changes appear with a delay.`,
      retryable: true,
    });
  }

  if (input.registryUpdating) {
    notices.push({
      id: 'registry-updating',
      severity: 'info',
      message:
        'The registry published a new version. The organogram below is the previous one until the refetch completes.',
      retryable: false,
    });
  }

  if (freshness.level === 'stale') {
    notices.push({
      id: 'stale',
      severity: 'warning',
      message: `No update has succeeded for ${formatAge(
        freshness.ageMs ?? 0,
      )}. What is shown may no longer reflect the organization.`,
      retryable: true,
    });
  }

  // A failure with a snapshot in hand is a background failure: the view keeps
  // the last known organogram and says that it stopped agreeing with the server.
  if (input.error !== null) {
    const failure = describeFailure(input.error);
    notices.push({
      id: 'update-failed',
      severity: 'warning',
      message: `The last update attempt failed. ${failure.detail}`,
      retryable: failure.retryable,
    });
  }

  return notices;
}

/** Compact age of a timestamp, shared so every view says «2m» the same way. */
export function formatAge(ageMs: number): string {
  const seconds = Math.round(ageMs / 1_000);
  if (seconds < 60) {
    return `${seconds}s`;
  }

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes}m`;
  }

  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}

export function formatSeconds(intervalMs: number): string {
  const seconds = intervalMs / 1_000;
  return `${Number.isInteger(seconds) ? seconds : seconds.toFixed(1)}s`;
}
