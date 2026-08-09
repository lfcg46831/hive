/**
 * Typed client for the public SignalR hub `/api/v1/organization-updates`.
 *
 * Realtime is an optimization, never the source of truth: a new or restored
 * connection recovers neither subscriptions nor missed events, so this client
 * resubscribes explicitly and asks the caller to refetch the REST snapshot
 * through `onSnapshotRequired`.
 */

import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import type {
  InboxChangedNotification,
  OrganizationPositionState,
  OrganogramChangedNotification,
  PositionStateChangedNotification,
} from './contracts.js';

export const ORGANIZATION_UPDATES_HUB_PATH = '/api/v1/organization-updates';

export const ORGANIZATION_UPDATES_METHODS = {
  subscribe: 'SubscribeToOrganization',
  unsubscribe: 'UnsubscribeFromOrganization',
  subscribeInbox: 'SubscribeToInbox',
  unsubscribeInbox: 'UnsubscribeFromInbox',
} as const;

export const ORGANIZATION_UPDATES_EVENTS = {
  organogramChanged: 'OrganogramChanged',
  positionStateChanged: 'PositionStateChanged',
  inboxChanged: 'InboxChanged',
} as const;

/** Which of the hub's two subscriptions a client wants. */
export type OrganizationUpdatesScope = 'organization' | 'inbox';

export type OrganizationUpdatesStatus =
  | 'connecting'
  | 'live'
  | 'reconnecting'
  | 'disconnected';

/** Why the caller must refetch the authoritative REST snapshot. */
export type SnapshotReason = 'subscribed' | 'reconnected';

/** Structural view of the SignalR connection, so tests can supply a double. */
export interface HubConnectionLike {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(methodName: string, ...args: unknown[]): Promise<unknown>;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  on(methodName: string, handler: (...args: any[]) => void): void;
  onreconnecting(callback: (error?: Error) => void): void;
  onreconnected(callback: (connectionId?: string) => void): void;
  onclose(callback: (error?: Error) => void): void;
}

export interface OrganizationUpdatesHandlers {
  onOrganogramChanged?(notification: OrganogramChangedNotification): void;
  onPositionStateChanged?(notification: PositionStateChangedNotification): void;
  onInboxChanged?(notification: InboxChangedNotification): void;
  onStatusChanged?(status: OrganizationUpdatesStatus, error?: Error): void;
  onSnapshotRequired?(reason: SnapshotReason): void;
}

export interface OrganizationUpdatesClientOptions {
  readonly baseUrl: string;
  readonly organizationId: string;
  readonly token: string | (() => string | Promise<string>);
  readonly handlers: OrganizationUpdatesHandlers;
  /**
   * Which hub subscription to hold. Defaults to the organization scope so the
   * organogram view is unchanged by the arrival of the inbox.
   */
  readonly scope?: OrganizationUpdatesScope;
  /** Injected for tests; defaults to a reconnecting SignalR connection. */
  readonly connectionFactory?: (hubUrl: string) => HubConnectionLike;
}

export interface OrganizationUpdatesClient {
  readonly status: OrganizationUpdatesStatus;
  readonly hubUrl: string;
  start(): Promise<void>;
  stop(): Promise<void>;
}

export function createOrganizationUpdatesClient(
  options: OrganizationUpdatesClientOptions,
): OrganizationUpdatesClient {
  const hubUrl = `${options.baseUrl.trim().replace(/\/+$/, '')}${ORGANIZATION_UPDATES_HUB_PATH}`;
  const { handlers, organizationId } = options;
  const scope: OrganizationUpdatesScope = options.scope ?? 'organization';
  const methods =
    scope === 'inbox'
      ? {
          subscribe: ORGANIZATION_UPDATES_METHODS.subscribeInbox,
          unsubscribe: ORGANIZATION_UPDATES_METHODS.unsubscribeInbox,
        }
      : {
          subscribe: ORGANIZATION_UPDATES_METHODS.subscribe,
          unsubscribe: ORGANIZATION_UPDATES_METHODS.unsubscribe,
        };
  const connection = (options.connectionFactory ?? defaultConnectionFactory(options.token))(
    hubUrl,
  );
  let status: OrganizationUpdatesStatus = 'disconnected';

  function setStatus(next: OrganizationUpdatesStatus, error?: Error): void {
    status = next;
    handlers.onStatusChanged?.(next, error);
  }

  connection.on(ORGANIZATION_UPDATES_EVENTS.organogramChanged, (notification) => {
    handlers.onOrganogramChanged?.(notification as OrganogramChangedNotification);
  });
  connection.on(ORGANIZATION_UPDATES_EVENTS.positionStateChanged, (notification) => {
    handlers.onPositionStateChanged?.(notification as PositionStateChangedNotification);
  });
  connection.on(ORGANIZATION_UPDATES_EVENTS.inboxChanged, (notification) => {
    handlers.onInboxChanged?.(notification as InboxChangedNotification);
  });
  connection.onreconnecting((error) => setStatus('reconnecting', error));
  connection.onclose((error) => setStatus('disconnected', error));
  connection.onreconnected(() => {
    void subscribe('reconnected');
  });

  async function subscribe(reason: SnapshotReason): Promise<void> {
    await connection.invoke(methods.subscribe, organizationId);
    setStatus('live');
    handlers.onSnapshotRequired?.(reason);
  }

  return {
    get status() {
      return status;
    },
    hubUrl,
    async start() {
      setStatus('connecting');
      await connection.start();
      await subscribe('subscribed');
    },
    async stop() {
      try {
        await connection.invoke(methods.unsubscribe, organizationId);
      } finally {
        await connection.stop();
        setStatus('disconnected');
      }
    },
  };
}

/**
 * True when `incoming` supersedes `known` for the same position. Position
 * sequences are monotonic, so out-of-order or replayed notifications are
 * discarded instead of moving the UI backwards.
 */
export function isNewerPositionState(
  known: OrganizationPositionState | undefined,
  incoming: OrganizationPositionState,
): boolean {
  if (known === undefined) {
    return true;
  }

  if (known.position_id !== incoming.position_id) {
    return false;
  }

  return incoming.sequence > known.sequence;
}

/**
 * True when `incoming` is a notification the principal has not already acted on.
 * Inbox sequences are monotonic per principal, so a replayed or out-of-order
 * notification is dropped rather than triggering a redundant refetch.
 */
export function isNewerInboxNotification(
  knownSequence: number | null,
  incoming: InboxChangedNotification,
): boolean {
  return knownSequence === null || incoming.sequence > knownSequence;
}

/**
 * True when notifications were missed between `knownSequence` and `incoming`.
 * The REST snapshot is refetched either way; this only lets the view say that
 * it is recovering from a gap instead of silently papering over one.
 */
export function hasInboxSequenceGap(
  knownSequence: number | null,
  incoming: InboxChangedNotification,
): boolean {
  return knownSequence !== null && incoming.sequence > knownSequence + 1;
}

function defaultConnectionFactory(
  token: string | (() => string | Promise<string>),
): (hubUrl: string) => HubConnectionLike {
  return (hubUrl) =>
    new HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => (typeof token === 'function' ? token() : token),
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
}
