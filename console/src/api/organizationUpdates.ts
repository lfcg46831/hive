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
  OrganizationPositionState,
  OrganogramChangedNotification,
  PositionStateChangedNotification,
} from './contracts.js';

export const ORGANIZATION_UPDATES_HUB_PATH = '/api/v1/organization-updates';

export const ORGANIZATION_UPDATES_METHODS = {
  subscribe: 'SubscribeToOrganization',
  unsubscribe: 'UnsubscribeFromOrganization',
} as const;

export const ORGANIZATION_UPDATES_EVENTS = {
  organogramChanged: 'OrganogramChanged',
  positionStateChanged: 'PositionStateChanged',
} as const;

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
  onStatusChanged?(status: OrganizationUpdatesStatus, error?: Error): void;
  onSnapshotRequired?(reason: SnapshotReason): void;
}

export interface OrganizationUpdatesClientOptions {
  readonly baseUrl: string;
  readonly organizationId: string;
  readonly token: string | (() => string | Promise<string>);
  readonly handlers: OrganizationUpdatesHandlers;
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
  connection.onreconnecting((error) => setStatus('reconnecting', error));
  connection.onclose((error) => setStatus('disconnected', error));
  connection.onreconnected(() => {
    void subscribe('reconnected');
  });

  async function subscribe(reason: SnapshotReason): Promise<void> {
    await connection.invoke(ORGANIZATION_UPDATES_METHODS.subscribe, organizationId);
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
        await connection.invoke(ORGANIZATION_UPDATES_METHODS.unsubscribe, organizationId);
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
