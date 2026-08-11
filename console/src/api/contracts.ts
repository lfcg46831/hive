/**
 * Wire contracts of the public HIVE API (`/api/v1`).
 *
 * These types mirror the published OpenAPI document one-to-one, including the
 * snake_case JSON property names. `openapiParity.contract.test.ts` verifies the
 * mirror mechanically against the exported document, so this file is never a
 * hand-maintained guess: a backend contract change fails the parity check.
 *
 * Nothing here may describe the private `/internal` surface.
 */

/** ISO-8601 timestamp in UTC, as serialized by the API. */
export type UtcTimestamp = string;

/** Occupant kinds declared by the organization registry. */
export type OrganizationOccupantType = 'AiAgent' | 'Human';

/**
 * Operational states in canonical precedence order, strongest first.
 * Precedence is resolved by the API; clients never re-derive it.
 */
export type PositionOperationalState =
  | 'Offline'
  | 'Blocked'
  | 'WaitingHuman'
  | 'Working'
  | 'Idle';

/** Monotonic version and fingerprint of the materialized registry snapshot. */
export interface RegistryVersion {
  version: number;
  fingerprint: string;
}

export interface OrganizationSummary {
  id: string;
  name: string | null;
  root_unit_id: string;
  root_position_id: string;
}

export interface OrganizationUnit {
  id: string;
  name: string | null;
  parent_unit_id: string | null;
  leadership_position_id: string;
}

export interface OrganizationOccupant {
  id: string | null;
  type: OrganizationOccupantType;
}

export interface PositionHierarchy {
  reports_to_position_id: string | null;
  direct_subordinate_position_ids: string[];
}

export interface PositionCorrelatedEvent {
  type: string;
  thread_id: string;
  occurred_at_utc: UtcTimestamp;
}

export interface OrganizationPositionState {
  position_id: string;
  state: PositionOperationalState;
  /** Monotonic per position; used to discard stale realtime notifications. */
  sequence: number;
  updated_at_utc: UtcTimestamp;
  last_correlated_event: PositionCorrelatedEvent | null;
}

export interface OrganizationPosition {
  id: string;
  name: string | null;
  unit_id: string;
  occupant: OrganizationOccupant;
  hierarchy: PositionHierarchy;
  operational_state: OrganizationPositionState;
}

export interface OrganogramResponse {
  registry: RegistryVersion;
  generated_at_utc: UtcTimestamp;
  root_unit_id: string;
  organization: OrganizationSummary;
  units: OrganizationUnit[];
  positions: OrganizationPosition[];
}

export interface PositionDetailResponse {
  registry: RegistryVersion;
  generated_at_utc: UtcTimestamp;
  position: OrganizationPosition;
}

export interface PositionStatesResponse {
  registry: RegistryVersion;
  generated_at_utc: UtcTimestamp;
  /** Staleness signal: timestamp of the last event applied by the projection. */
  last_event_applied_at_utc: UtcTimestamp | null;
  states: OrganizationPositionState[];
}

/** RFC 7807 payload returned by every failing public endpoint. */
export interface ProblemDetails {
  type?: string | null;
  title?: string | null;
  status?: number | null;
  detail?: string | null;
  instance?: string | null;
  [extension: string]: unknown;
}

/* ── Inbox (US-F1-02) ─────────────────────────────────────────────────────── */

/** Organizational message kinds the human inbox can carry. */
export type InboxMessageType =
  | 'Directive'
  | 'Report'
  | 'Escalation'
  | 'Memo'
  | 'PeerRequest'
  | 'PeerResponse'
  | 'ApprovalRequest'
  | 'ApprovalDecision';

export type InboxReportKind = 'progress' | 'done';

/** Closed canonical content returned only by the principal-scoped detail route. */
export type InboxMessageContent =
  | InboxDirectiveMessageContent
  | InboxReportMessageContent
  | InboxEscalationMessageContent
  | InboxMemoMessageContent
  | InboxPeerRequestMessageContent
  | InboxPeerResponseMessageContent
  | InboxApprovalRequestMessageContent
  | InboxApprovalDecisionMessageContent;

export interface InboxDirectiveMessageContent {
  type: 'Directive';
  objective: string;
  context: string;
}

export interface InboxReportMessageContent {
  type: 'Report';
  body: string;
  kind: InboxReportKind;
}

export interface InboxEscalationMessageContent {
  type: 'Escalation';
  issue: string;
  context: string;
}

export interface InboxMemoMessageContent {
  type: 'Memo';
  body: string;
}

export interface InboxPeerRequestMessageContent {
  type: 'PeerRequest';
  ask: string;
}

export interface InboxPeerResponseMessageContent {
  type: 'PeerResponse';
  body: string;
}

export interface InboxApprovalRequestMessageContent {
  type: 'ApprovalRequest';
  action: string;
  justification: string;
}

export interface InboxApprovalDecisionMessageContent {
  type: 'ApprovalDecision';
  /** Omitted when the canonical decision carries no reason. */
  reason?: string | null;
}

export type InboxMessageEndpointType = 'Position' | 'OrganizationOwner';

export type InboxPriority = 'Low' | 'Normal' | 'High' | 'Critical';

/** Person-scoped interaction state. Owned by the caller, never derived. */
export type InboxReadState = 'Unread' | 'Read';

/**
 * Response state of an item. `NotApplicable` means the closed reply mapping has
 * no response for this message kind — the console reads it, it does not infer it.
 */
export type InboxResponseState =
  | 'NotApplicable'
  | 'AwaitingResponse'
  | 'InProgress'
  | 'Responded';

export type InboxApprovalState = 'Pending' | 'Approved' | 'Rejected' | 'Expired';

/** Whether a deadline policy has already emitted a reminder for the item. */
export type InboxReminderState = 'None' | 'Sent';

export interface InboxMessageEndpoint {
  type: InboxMessageEndpointType;
  position_id: string | null;
}

export interface InboxApprovalMetadata {
  request_id: string;
  action: string;
  policy_ref: string;
  state: InboxApprovalState;
  /**
   * Server-resolved authority of this principal over this request. The console
   * only reflects it; the emission path validates authority again regardless.
   */
  can_decide: boolean;
  decision_message_id: string | null;
  decided_at_utc: UtcTimestamp | null;
}

export interface InboxItem {
  item_id: string;
  message_id: string;
  assigned_position_id: string;
  type: InboxMessageType;
  origin: InboxMessageEndpoint;
  destination: InboxMessageEndpoint;
  thread_id: string;
  priority: InboxPriority;
  sent_at_utc: UtcTimestamp;
  deadline_at_utc: UtcTimestamp | null;
  is_expired: boolean;
  reminder_state: InboxReminderState;
  last_reminder_at_utc: UtcTimestamp | null;
  is_delegated: boolean;
  read_state: InboxReadState;
  response_state: InboxResponseState;
  approval: InboxApprovalMetadata | null;
}

export interface InboxPage {
  generated_at_utc: UtcTimestamp;
  /** Staleness signal: timestamp of the last event applied by the projection. */
  last_event_applied_at_utc: UtcTimestamp | null;
  page_size: number;
  /** Opaque continuation token; null on the last page. */
  next_cursor: string | null;
  items: InboxItem[];
}

export interface InboxItemResponse {
  generated_at_utc: UtcTimestamp;
  last_event_applied_at_utc: UtcTimestamp | null;
  item: InboxItem;
  draft_text: string | null;
  content: InboxMessageContent | null;
}

/** Body of `POST /inbox/{itemId}/draft`. */
export interface InboxDraftRequest {
  /** Null starts a response, empty clears the draft, text saves it. */
  body?: string | null;
}

/** Body of `POST /inbox/{itemId}/reply`. */
export interface InboxReplyRequest {
  body?: string | null;
  /** Required as `progress` or `done` when replying to a Directive. */
  report_kind?: string | null;
}

/** Body of `POST /inbox/{itemId}/decision`. */
export interface InboxDecisionRequest {
  approved?: boolean | null;
  reason?: string | null;
}

/** Interaction state after a read/unread/draft action. No message is emitted. */
export interface InboxInteractionResponse {
  generated_at_utc: UtcTimestamp;
  last_event_applied_at_utc: UtcTimestamp | null;
  item_id: string;
  read_state: InboxReadState;
  response_state: InboxResponseState;
  draft_text: string | null;
  interaction_updated_at_utc: UtcTimestamp;
}

/** Metadata of the canonical message the occupied position emitted. */
export interface InboxReplyResponse {
  source_message_id: string;
  message_id: string;
  type: InboxMessageType;
  from_position_id: string;
  to_position_id: string;
  thread_id: string;
  directive_id: string | null;
}

/** Metadata of the canonical `ApprovalDecision` the occupied position emitted. */
export interface InboxDecisionResponse {
  request_id: string;
  message_id: string;
  approved: boolean;
  reason: string | null;
  from_position_id: string;
  to_position_id: string;
  thread_id: string;
}

/**
 * Structured rejection emitted by the governance validator, carried in the
 * `errors` extension of a Problem Details response.
 */
export interface InboxEmissionError {
  code: string;
  path: string;
  reason: string;
}

/** SignalR notification: the published registry snapshot changed. */
export interface OrganogramChangedNotification {
  organization_id: string;
  registry: RegistryVersion;
  changed_at_utc: UtcTimestamp;
}

/** SignalR notification: one position moved to a new operational state. */
export interface PositionStateChangedNotification {
  organization_id: string;
  state: OrganizationPositionState;
}

/** The kind of committed inbox change that invalidated the REST snapshot. */
export type InboxChangeType =
  | 'NewItem'
  | 'ReadStateChanged'
  | 'ResponseStateChanged'
  | 'ApprovalPending'
  | 'DecisionIssued'
  | 'DeadlineApproaching';

/**
 * SignalR notification: an inbox change was committed for this principal.
 *
 * Deliberately a bare invalidation signal — it carries no item payload, so the
 * console cannot mistake it for data and always goes back to REST.
 */
export interface InboxChangedNotification {
  /** Monotonic per principal; a gap means notifications were missed. */
  sequence: number;
  organization_id: string;
  item_id: string;
  assigned_position_id: string;
  change_type: InboxChangeType;
  changed_at_utc: UtcTimestamp;
}
