/**
 * Pure presentation rules of one inbox item.
 *
 * Everything here is a reading of the published DTO, never a re-derivation of an
 * organizational fact. Two rules are load-bearing:
 *
 * - Expiry is only ever claimed when the API says `is_expired`. The console's
 *   clock may well be past a deadline that the projection has not yet resolved,
 *   and «the deadline passed» and «the item expired» are different statements.
 * - What a person may do with an item comes from `response_state` and
 *   `approval.can_decide`, both resolved server-side. The console reflects them
 *   and the emission path validates authority again regardless of what was shown.
 */

import type {
  InboxItem,
  InboxMessageType,
  InboxPriority,
  InboxResponseState,
} from '../../api/index.js';

/** How close an item is to the end of its deadline window. */
export type DeadlineUrgency = 'none' | 'scheduled' | 'due-soon' | 'due-now' | 'expired';

export interface DeadlineView {
  readonly urgency: DeadlineUrgency;
  readonly label: string;
  /** Milliseconds until the deadline; negative once it has passed. */
  readonly remainingMs: number | null;
}

/** Window in which a deadline is presented as imminent rather than scheduled. */
export const DUE_SOON_MS = 60 * 60 * 1_000;

export function describeDeadline(item: InboxItem, nowMs: number): DeadlineView {
  if (item.is_expired) {
    return {
      urgency: 'expired',
      label: 'Expired',
      remainingMs: remainingMs(item.deadline_at_utc, nowMs),
    };
  }

  if (item.deadline_at_utc === null) {
    return { urgency: 'none', label: 'No deadline', remainingMs: null };
  }

  const remaining = remainingMs(item.deadline_at_utc, nowMs);
  if (remaining === null) {
    // An unparsable timestamp is reported, not hidden behind a placeholder.
    return { urgency: 'none', label: item.deadline_at_utc, remainingMs: null };
  }

  if (remaining <= 0) {
    // Deliberately not «expired»: only the projection decides that.
    return { urgency: 'due-now', label: `Due ${formatDuration(-remaining)} ago`, remainingMs: remaining };
  }

  return remaining <= DUE_SOON_MS
    ? { urgency: 'due-soon', label: `Due in ${formatDuration(remaining)}`, remainingMs: remaining }
    : { urgency: 'scheduled', label: `Due in ${formatDuration(remaining)}`, remainingMs: remaining };
}

/**
 * The closed F1 response mapping of `US-F1-02-T07`, as published in the reply
 * contract. Message kinds absent from it have no reply, which is also what their
 * `response_state` reports.
 */
const REPLY_MAPPING: Partial<Readonly<Record<InboxMessageType, InboxMessageType>>> = {
  Directive: 'Report',
  PeerRequest: 'PeerResponse',
  Escalation: 'Directive',
};

export type ReplyCapability =
  | { readonly kind: 'unavailable'; readonly reason: string }
  | {
      readonly kind: 'available';
      /** The canonical message the occupied position will emit. */
      readonly emits: InboxMessageType;
      /** A Directive reply must declare whether it reports progress or completion. */
      readonly requiresReportKind: boolean;
      readonly inProgress: boolean;
    };

export function describeReply(item: InboxItem): ReplyCapability {
  const emits = REPLY_MAPPING[item.type];
  if (item.response_state === 'NotApplicable' || emits === undefined) {
    return {
      kind: 'unavailable',
      reason: 'This message kind has no correlated response in the current mapping.',
    };
  }

  if (item.response_state === 'Responded') {
    return {
      kind: 'unavailable',
      reason: 'A correlated response has already been emitted in this thread.',
    };
  }

  return {
    kind: 'available',
    emits,
    requiresReportKind: item.type === 'Directive',
    inProgress: item.response_state === 'InProgress',
  };
}

export type DecisionCapability =
  | { readonly kind: 'unavailable'; readonly reason: string }
  | { readonly kind: 'available'; readonly action: string; readonly policyRef: string };

export function describeDecision(item: InboxItem): DecisionCapability {
  const approval = item.approval;
  if (approval === null || item.type !== 'ApprovalRequest') {
    return { kind: 'unavailable', reason: 'This item is not an approval request.' };
  }

  if (approval.can_decide) {
    return { kind: 'available', action: approval.action, policyRef: approval.policy_ref };
  }

  switch (approval.state) {
    case 'Approved':
      return { kind: 'unavailable', reason: 'This request was already approved.' };
    case 'Rejected':
      return { kind: 'unavailable', reason: 'This request was already rejected.' };
    case 'Expired':
      return { kind: 'unavailable', reason: 'The approval window for this request has closed.' };
    case 'Pending':
      // Visible without authority: the request is addressed to a position of
      // this person, but the policy resolves a different approver.
      return {
        kind: 'unavailable',
        reason: 'The approval policy does not name this position as the approver.',
      };
  }
}

const MESSAGE_TYPE_LABELS: Readonly<Record<InboxMessageType, string>> = {
  Directive: 'Directive',
  Report: 'Report',
  Escalation: 'Escalation',
  Memo: 'Memo',
  PeerRequest: 'Peer request',
  PeerResponse: 'Peer response',
  ApprovalRequest: 'Approval request',
  ApprovalDecision: 'Approval decision',
};

const RESPONSE_STATE_LABELS: Readonly<Record<InboxResponseState, string>> = {
  NotApplicable: 'No response expected',
  AwaitingResponse: 'Awaiting response',
  InProgress: 'Response in progress',
  Responded: 'Responded',
};

export const PRIORITY_ORDER: readonly InboxPriority[] = ['Low', 'Normal', 'High', 'Critical'];

export const MESSAGE_TYPE_ORDER: readonly InboxMessageType[] = [
  'Directive',
  'Report',
  'Escalation',
  'Memo',
  'PeerRequest',
  'PeerResponse',
  'ApprovalRequest',
  'ApprovalDecision',
];

export function messageTypeLabel(type: InboxMessageType): string {
  return MESSAGE_TYPE_LABELS[type];
}

export function responseStateLabel(state: InboxResponseState): string {
  return RESPONSE_STATE_LABELS[state];
}

/** Human label of an endpoint, which is a position or the organization owner. */
export function endpointLabel(endpoint: InboxItem['origin']): string {
  return endpoint.type === 'OrganizationOwner'
    ? 'Organization owner'
    : (endpoint.position_id ?? 'Unknown position');
}

function remainingMs(deadline: string | null, nowMs: number): number | null {
  if (deadline === null) {
    return null;
  }

  const parsed = Date.parse(deadline);
  return Number.isNaN(parsed) ? null : parsed - nowMs;
}

function formatDuration(durationMs: number): string {
  const seconds = Math.max(0, Math.round(durationMs / 1_000));
  if (seconds < 60) {
    return `${seconds}s`;
  }

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes}m`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h ${minutes % 60}m`;
  }

  return `${Math.floor(hours / 24)}d ${hours % 24}h`;
}
