import { describe, expect, it } from 'vitest';

import type {
  OrganizationPositionState,
  PositionStateChangedNotification,
} from './contracts.js';
import type { HubConnectionLike, SnapshotReason } from './organizationUpdates.js';
import {
  ORGANIZATION_UPDATES_HUB_PATH,
  createOrganizationUpdatesClient,
  isNewerPositionState,
} from './organizationUpdates.js';

class FakeHubConnection implements HubConnectionLike {
  readonly invocations: Array<{ method: string; args: unknown[] }> = [];
  started = false;

  private readonly handlers = new Map<string, (...args: unknown[]) => void>();
  private reconnected: (connectionId?: string) => void = () => {};
  private reconnecting: (error?: Error) => void = () => {};
  private closed: (error?: Error) => void = () => {};

  start(): Promise<void> {
    this.started = true;
    return Promise.resolve();
  }

  stop(): Promise<void> {
    this.started = false;
    return Promise.resolve();
  }

  invoke(methodName: string, ...args: unknown[]): Promise<unknown> {
    this.invocations.push({ method: methodName, args });
    return Promise.resolve(undefined);
  }

  on(methodName: string, handler: (...args: unknown[]) => void): void {
    this.handlers.set(methodName, handler);
  }

  onreconnecting(callback: (error?: Error) => void): void {
    this.reconnecting = callback;
  }

  onreconnected(callback: (connectionId?: string) => void): void {
    this.reconnected = callback;
  }

  onclose(callback: (error?: Error) => void): void {
    this.closed = callback;
  }

  emit(methodName: string, payload: unknown): void {
    this.handlers.get(methodName)?.(payload);
  }

  dropAndRecover(): void {
    this.reconnecting(new Error('transport closed'));
    this.reconnected('connection-2');
  }

  close(error?: Error): void {
    this.closed(error);
  }
}

function positionState(sequence: number): OrganizationPositionState {
  return {
    position_id: 'head-of-delivery',
    state: 'Working',
    sequence,
    updated_at_utc: '2026-08-03T10:00:00+00:00',
    last_correlated_event: null,
  };
}

function buildClient() {
  const connection = new FakeHubConnection();
  const snapshotRequests: SnapshotReason[] = [];
  const statuses: string[] = [];
  const received: PositionStateChangedNotification[] = [];
  const client = createOrganizationUpdatesClient({
    baseUrl: 'https://hive.example.com/',
    organizationId: 'acme-delivery',
    token: 'secret',
    connectionFactory: () => connection,
    handlers: {
      onPositionStateChanged: (notification) => received.push(notification),
      onSnapshotRequired: (reason) => snapshotRequests.push(reason),
      onStatusChanged: (status) => statuses.push(status),
    },
  });
  return { client, connection, snapshotRequests, statuses, received };
}

describe('createOrganizationUpdatesClient', () => {
  it('connects to the public hub path and subscribes to the authorized organization', async () => {
    const { client, connection, statuses, snapshotRequests } = buildClient();

    await client.start();

    expect(client.hubUrl).toBe(`https://hive.example.com${ORGANIZATION_UPDATES_HUB_PATH}`);
    expect(connection.invocations).toEqual([
      { method: 'SubscribeToOrganization', args: ['acme-delivery'] },
    ]);
    expect(statuses).toEqual(['connecting', 'live']);
    expect(snapshotRequests).toEqual(['subscribed']);
    expect(client.status).toBe('live');
  });

  it('resubscribes and asks for a fresh snapshot after a reconnect', async () => {
    const { client, connection, snapshotRequests, statuses } = buildClient();
    await client.start();

    connection.dropAndRecover();
    await Promise.resolve();

    expect(connection.invocations.map((invocation) => invocation.method)).toEqual([
      'SubscribeToOrganization',
      'SubscribeToOrganization',
    ]);
    expect(snapshotRequests).toEqual(['subscribed', 'reconnected']);
    expect(statuses).toEqual(['connecting', 'live', 'reconnecting', 'live']);
  });

  it('reports a dropped connection as degraded', async () => {
    const { client, connection, statuses } = buildClient();
    await client.start();

    connection.close(new Error('server shutdown'));

    expect(client.status).toBe('disconnected');
    expect(statuses.at(-1)).toBe('disconnected');
  });

  it('delivers position-state notifications to the handler', async () => {
    const { client, connection, received } = buildClient();
    await client.start();

    connection.emit('PositionStateChanged', {
      organization_id: 'acme-delivery',
      state: positionState(4),
    });

    expect(received).toEqual([
      { organization_id: 'acme-delivery', state: positionState(4) },
    ]);
  });

  it('unsubscribes before stopping', async () => {
    const { client, connection } = buildClient();
    await client.start();

    await client.stop();

    expect(connection.invocations.at(-1)).toEqual({
      method: 'UnsubscribeFromOrganization',
      args: ['acme-delivery'],
    });
    expect(connection.started).toBe(false);
    expect(client.status).toBe('disconnected');
  });
});

describe('isNewerPositionState', () => {
  it('accepts the first state and any higher sequence for the same position', () => {
    expect(isNewerPositionState(undefined, positionState(1))).toBe(true);
    expect(isNewerPositionState(positionState(1), positionState(2))).toBe(true);
  });

  it('discards replayed, stale and mismatched notifications', () => {
    expect(isNewerPositionState(positionState(2), positionState(2))).toBe(false);
    expect(isNewerPositionState(positionState(3), positionState(2))).toBe(false);
    expect(
      isNewerPositionState(positionState(1), { ...positionState(9), position_id: 'other' }),
    ).toBe(false);
  });
});
