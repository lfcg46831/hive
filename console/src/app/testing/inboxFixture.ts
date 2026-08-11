/**
 * Inbox fixtures and a fake public inbox API (US-F1-02-T14).
 *
 * Two things live here. The builders shape values exactly like the public wire
 * contract in `api/contracts.ts`, because a fixture that drifts from the
 * contract makes the tests agree with an API the console never talks to. The
 * server is a small stand-in for the eight documented inbox routes that answers
 * from a mutable list of items: read state, drafts and paging behave the way the
 * API describes them, while a reply or a decision answers with the metadata of
 * the emitted message and deliberately does not update the derived state — the
 * projection lag is real, and the console must not paper over it.
 *
 * Not part of the shipped bundle: only test files import it.
 */

import type {
  InboxApprovalMetadata,
  InboxApprovalState,
  InboxChangeType,
  InboxChangedNotification,
  InboxDecisionResponse,
  InboxInteractionResponse,
  InboxItem,
  InboxItemResponse,
  InboxMessageContent,
  InboxMessageEndpoint,
  InboxMessageType,
  InboxPage,
  InboxPriority,
  InboxReadState,
  InboxReminderState,
  InboxReplyResponse,
  InboxResponseState,
} from '../../api/index.js';

export const INBOX_ORGANIZATION_ID = 'acme-delivery';
/** The instant the tests pin their clock to. */
export const INBOX_NOW_UTC = '2026-08-10T09:00:05.000Z';
/** Snapshot timestamp: five seconds old, so freshness is a property of the test. */
export const INBOX_GENERATED_AT_UTC = '2026-08-10T09:00:00.000Z';
export const INBOX_PROJECTION_APPLIED_AT_UTC = '2026-08-10T08:59:58.000Z';
/** The position of the person these fixtures belong to. */
export const READER_POSITION_ID = 'bug-triage';

/** A timestamp relative to the pinned clock, for deadlines and reminders. */
export function atOffset(offsetMs: number): string {
  return new Date(Date.parse(INBOX_NOW_UTC) + offsetMs).toISOString();
}

export interface InboxItemOptions {
  readonly id: string;
  readonly messageId?: string;
  readonly type?: InboxMessageType;
  /** Position of the reader the item is addressed to. */
  readonly assignedPositionId?: string;
  /** Sender: a position id, or an explicit endpoint for the organization owner. */
  readonly origin?: string | InboxMessageEndpoint;
  readonly threadId?: string;
  readonly priority?: InboxPriority;
  readonly sentAtUtc?: string;
  readonly deadlineAtUtc?: string | null;
  readonly isExpired?: boolean;
  readonly reminderState?: InboxReminderState;
  readonly lastReminderAtUtc?: string | null;
  readonly isDelegated?: boolean;
  readonly readState?: InboxReadState;
  readonly responseState?: InboxResponseState;
  readonly approval?: InboxApprovalMetadata | null;
}

export function inboxItem(options: InboxItemOptions): InboxItem {
  const assignedPositionId = options.assignedPositionId ?? READER_POSITION_ID;

  return {
    item_id: options.id,
    message_id: options.messageId ?? `message-${options.id}`,
    assigned_position_id: assignedPositionId,
    type: options.type ?? 'Directive',
    origin: endpoint(options.origin ?? 'delivery-lead'),
    destination: endpoint(assignedPositionId),
    thread_id: options.threadId ?? `thread-${options.id}`,
    priority: options.priority ?? 'Normal',
    sent_at_utc: options.sentAtUtc ?? '2026-08-10T08:30:00.000Z',
    deadline_at_utc: options.deadlineAtUtc ?? null,
    is_expired: options.isExpired ?? false,
    reminder_state: options.reminderState ?? 'None',
    last_reminder_at_utc: options.lastReminderAtUtc ?? null,
    is_delegated: options.isDelegated ?? false,
    read_state: options.readState ?? 'Unread',
    response_state: options.responseState ?? 'AwaitingResponse',
    approval: options.approval ?? null,
  };
}

export interface InboxApprovalOptions {
  readonly requestId?: string;
  readonly action?: string;
  readonly policyRef?: string;
  readonly state?: InboxApprovalState;
  /** Server-resolved authority of the principal over this request. */
  readonly canDecide: boolean;
  readonly decisionMessageId?: string | null;
  readonly decidedAtUtc?: string | null;
}

export function inboxApproval(options: InboxApprovalOptions): InboxApprovalMetadata {
  return {
    request_id: options.requestId ?? 'request-1',
    action: options.action ?? 'Deploy hotfix 4.2.1 to production',
    policy_ref: options.policyRef ?? 'policies/deployment-approval',
    state: options.state ?? 'Pending',
    can_decide: options.canDecide,
    decision_message_id: options.decisionMessageId ?? null,
    decided_at_utc: options.decidedAtUtc ?? null,
  };
}

/** An approval request addressed to the reader, decidable or not. */
export function approvalRequest(options: {
  readonly id: string;
  readonly canDecide: boolean;
  readonly state?: InboxApprovalState;
  readonly deadlineAtUtc?: string | null;
}): InboxItem {
  return inboxItem({
    id: options.id,
    type: 'ApprovalRequest',
    responseState: 'NotApplicable',
    ...(options.deadlineAtUtc === undefined ? {} : { deadlineAtUtc: options.deadlineAtUtc }),
    approval: inboxApproval({
      requestId: `request-${options.id}`,
      canDecide: options.canDecide,
      ...(options.state === undefined ? {} : { state: options.state }),
    }),
  });
}

export function inboxPage(
  items: readonly InboxItem[],
  overrides: Partial<InboxPage> = {},
): InboxPage {
  return {
    generated_at_utc: INBOX_GENERATED_AT_UTC,
    last_event_applied_at_utc: INBOX_PROJECTION_APPLIED_AT_UTC,
    page_size: 25,
    next_cursor: null,
    items: [...items],
    ...overrides,
  };
}

export function inboxItemResponse(
  item: InboxItem,
  draftText: string | null = null,
  content: InboxMessageContent | null = null,
): InboxItemResponse {
  return {
    generated_at_utc: INBOX_GENERATED_AT_UTC,
    last_event_applied_at_utc: INBOX_PROJECTION_APPLIED_AT_UTC,
    item,
    draft_text: draftText,
    content,
  };
}

export function inboxInteraction(
  item: InboxItem,
  draftText: string | null = null,
): InboxInteractionResponse {
  return {
    generated_at_utc: INBOX_GENERATED_AT_UTC,
    last_event_applied_at_utc: INBOX_PROJECTION_APPLIED_AT_UTC,
    item_id: item.item_id,
    read_state: item.read_state,
    response_state: item.response_state,
    draft_text: draftText,
    interaction_updated_at_utc: INBOX_NOW_UTC,
  };
}

/** Canonical message the occupied position emitted in answer to `item`. */
export function inboxReplyResponse(
  item: InboxItem,
  type: InboxMessageType,
  overrides: Partial<InboxReplyResponse> = {},
): InboxReplyResponse {
  return {
    source_message_id: item.message_id,
    message_id: `emitted-${item.item_id}`,
    type,
    from_position_id: item.assigned_position_id,
    to_position_id: item.origin.position_id ?? 'organization-owner',
    thread_id: item.thread_id,
    directive_id: null,
    ...overrides,
  };
}

export function inboxDecisionResponse(
  item: InboxItem,
  approved: boolean,
  reason: string | null = null,
): InboxDecisionResponse {
  return {
    request_id: item.approval?.request_id ?? 'request-1',
    message_id: `decision-${item.item_id}`,
    approved,
    reason,
    from_position_id: item.assigned_position_id,
    to_position_id: item.origin.position_id ?? 'organization-owner',
    thread_id: item.thread_id,
  };
}

export function inboxNotification(options: {
  readonly sequence: number;
  readonly itemId?: string;
  readonly changeType?: InboxChangeType;
  readonly assignedPositionId?: string;
}): InboxChangedNotification {
  return {
    sequence: options.sequence,
    organization_id: INBOX_ORGANIZATION_ID,
    item_id: options.itemId ?? 'item-new',
    assigned_position_id: options.assignedPositionId ?? READER_POSITION_ID,
    change_type: options.changeType ?? 'NewItem',
    changed_at_utc: INBOX_NOW_UTC,
  };
}

/* ── Fake public inbox API ────────────────────────────────────────────────── */

export interface RecordedRequest {
  readonly method: string;
  readonly url: string;
  readonly path: string;
  readonly query: URLSearchParams;
  readonly headers: Record<string, string>;
  /** Parsed JSON body of a POST, or null for a GET. */
  readonly body: Record<string, unknown> | null;
}

export type RouteHandler = (request: RecordedRequest) => Response | Promise<Response>;

export interface InboxServer {
  readonly requests: readonly RecordedRequest[];
  /** The inbox as the projection currently holds it; tests mutate it directly. */
  items: InboxItem[];
  /** Persisted drafts by item, the way the read model holds them per principal. */
  readonly drafts: Map<string, string | null>;
  /** Bumped by every committed change, and carried by the list ETag. */
  version: number;
  /** Null models a projection that has not reported an applied event yet. */
  projectionAppliedAtUtc: string | null;
  /** Overridable routes; each defaults to the behaviour the contract documents. */
  list: RouteHandler;
  detail: RouteHandler;
  read: RouteHandler;
  draft: RouteHandler;
  reply: RouteHandler;
  decide: RouteHandler;
  fetch(input: string, init?: RequestInit): Promise<Response>;
  requestsTo(suffix: string): readonly RecordedRequest[];
  listRequests(): readonly RecordedRequest[];
  find(itemId: string): InboxItem | undefined;
}

export function createInboxServer(items: readonly InboxItem[] = []): InboxServer {
  const requests: RecordedRequest[] = [];

  const server: InboxServer = {
    requests,
    items: [...items],
    drafts: new Map<string, string | null>(),
    version: 1,
    projectionAppliedAtUtc: INBOX_PROJECTION_APPLIED_AT_UTC,

    list(request) {
      const pageSize = Number(request.query.get('page_size') ?? '25');
      const cursor = Number(request.query.get('cursor') ?? '0');
      const slice = server.items.slice(cursor, cursor + pageSize);
      const nextCursor = cursor + pageSize < server.items.length ? String(cursor + pageSize) : null;
      const etag = `"inbox-${server.version}"`;

      // Conditional polling only makes sense for the first page, which is the
      // only one the view ever polls.
      if (cursor === 0 && request.headers['if-none-match'] === etag) {
        return notModified(etag);
      }

      return jsonResponse(
        inboxPage(slice, {
          page_size: pageSize,
          next_cursor: nextCursor,
          last_event_applied_at_utc: server.projectionAppliedAtUtc,
        }),
        { headers: { etag } },
      );
    },

    detail(request) {
      const itemId = lastSegment(request.path);
      const item = server.find(itemId);
      return item === undefined
        ? problemResponse(404, 'Inbox item not found')
        : jsonResponse(inboxItemResponse(item, server.drafts.get(itemId) ?? null));
    },

    read(request) {
      const itemId = segmentBeforeLast(request.path);
      const item = requireItem(server, itemId);
      const read = request.path.endsWith('/read');
      const updated: InboxItem = { ...item, read_state: read ? 'Read' : 'Unread' };
      replace(server, updated);
      return jsonResponse(inboxInteraction(updated, server.drafts.get(itemId) ?? null));
    },

    draft(request) {
      const itemId = segmentBeforeLast(request.path);
      const item = requireItem(server, itemId);
      const body = request.body?.['body'];
      const text = typeof body === 'string' ? body : null;

      // Null starts a response, an empty string clears the draft, text saves it.
      const draft = text === null ? (server.drafts.get(itemId) ?? null) : text.length === 0 ? null : text;
      server.drafts.set(itemId, draft);
      const updated: InboxItem = {
        ...item,
        response_state: draft === null ? 'AwaitingResponse' : 'InProgress',
      };
      replace(server, updated);
      return jsonResponse(inboxInteraction(updated, draft));
    },

    reply(request) {
      const item = requireItem(server, segmentBeforeLast(request.path));
      server.version += 1;
      // 202 semantics: what comes back is the emitted message, not the new state
      // of the inbox. The projection catches up on its own time.
      return jsonResponse(inboxReplyResponse(item, replyTypeOf(item)));
    },

    decide(request) {
      const item = requireItem(server, segmentBeforeLast(request.path));
      server.version += 1;
      const approved = request.body?.['approved'] === true;
      const reason = request.body?.['reason'];
      return jsonResponse(
        inboxDecisionResponse(item, approved, typeof reason === 'string' ? reason : null),
      );
    },

    async fetch(input, init) {
      const request = record(requests, input, init);
      const path = request.path;

      if (path.endsWith('/read') || path.endsWith('/unread')) {
        return server.read(request);
      }

      if (path.endsWith('/draft')) {
        return server.draft(request);
      }

      if (path.endsWith('/reply')) {
        return server.reply(request);
      }

      if (path.endsWith('/decision')) {
        return server.decide(request);
      }

      if (path.endsWith('/inbox')) {
        return server.list(request);
      }

      if (path.includes('/inbox/')) {
        return server.detail(request);
      }

      throw new Error(`The console called an unexpected route: ${request.method} ${path}`);
    },

    requestsTo(suffix) {
      return requests.filter((request) => request.path.endsWith(suffix));
    },

    listRequests() {
      return requests.filter((request) => request.path.endsWith('/inbox'));
    },

    find(itemId) {
      return server.items.find((candidate) => candidate.item_id === itemId);
    },
  };

  return server;
}

export function jsonResponse(body: unknown, init: ResponseInit = {}): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    ...init,
    headers: { 'content-type': 'application/json', ...(init.headers ?? {}) },
  });
}

export function notModified(etag: string): Response {
  return new Response(null, { status: 304, headers: { etag } });
}

/** Problem Details, optionally carrying the governance validator's rejections. */
export function problemResponse(
  status: number,
  title: string,
  options: {
    readonly detail?: string;
    readonly errors?: readonly { code: string; path: string; reason: string }[];
  } = {},
): Response {
  return new Response(
    JSON.stringify({
      status,
      title,
      ...(options.detail === undefined ? {} : { detail: options.detail }),
      ...(options.errors === undefined ? {} : { errors: [...options.errors] }),
    }),
    { status, headers: { 'content-type': 'application/problem+json' } },
  );
}

function record(
  requests: RecordedRequest[],
  input: string,
  init: RequestInit | undefined,
): RecordedRequest {
  const url = new URL(String(input));
  const rawBody = init?.body;
  const request: RecordedRequest = {
    method: init?.method ?? 'GET',
    url: String(input),
    path: url.pathname,
    query: url.searchParams,
    headers: (init?.headers ?? {}) as Record<string, string>,
    body: typeof rawBody === 'string' ? (JSON.parse(rawBody) as Record<string, unknown>) : null,
  };
  requests.push(request);
  return request;
}

function requireItem(server: InboxServer, itemId: string): InboxItem {
  const item = server.find(itemId);
  if (item === undefined) {
    throw new Error(`The console acted on item ${itemId}, which the inbox does not list.`);
  }

  return item;
}

function replace(server: InboxServer, item: InboxItem): void {
  server.items = server.items.map((candidate) =>
    candidate.item_id === item.item_id ? item : candidate,
  );
  server.version += 1;
}

/** The closed F1 reply mapping, mirrored so the fake answers like the API. */
function replyTypeOf(item: InboxItem): InboxMessageType {
  switch (item.type) {
    case 'Directive':
      return 'Report';
    case 'PeerRequest':
      return 'PeerResponse';
    case 'Escalation':
      return 'Directive';
    default:
      throw new Error(`The console replied to a ${item.type}, which has no correlated response.`);
  }
}

function endpoint(value: string | InboxMessageEndpoint): InboxMessageEndpoint {
  return typeof value === 'string' ? { type: 'Position', position_id: value } : value;
}

function lastSegment(path: string): string {
  return decodeURIComponent(path.slice(path.lastIndexOf('/') + 1));
}

function segmentBeforeLast(path: string): string {
  return lastSegment(path.slice(0, path.lastIndexOf('/')));
}
