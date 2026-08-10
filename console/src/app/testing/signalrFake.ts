/**
 * SignalR double shared by the frontend view tests (US-F1-01-T14, US-F1-02-T14).
 *
 * It replaces the transport and nothing else: the console's own hub client keeps
 * running, so subscription, resubscription after a reconnect and the rule that a
 * notification sends the view back to REST stay under test instead of being
 * replaced by the double. Every method the client uses is here, and nothing more.
 *
 * Not part of the shipped bundle: only test files import it.
 */

export type HubHandler = (...args: unknown[]) => void;

export interface HubInvocation {
  readonly method: string;
  readonly args: readonly unknown[];
}

export class FakeHubConnection {
  readonly handlers = new Map<string, HubHandler>();
  readonly invocations: HubInvocation[] = [];
  startError: Error | null = null;
  started = false;

  private onReconnecting: (error?: Error) => void = () => undefined;
  private onReconnected: (connectionId?: string) => void = () => undefined;
  private onClosed: (error?: Error) => void = () => undefined;

  async start(): Promise<void> {
    if (this.startError !== null) {
      throw this.startError;
    }

    this.started = true;
  }

  async stop(): Promise<void> {
    this.started = false;
  }

  async invoke(method: string, ...args: unknown[]): Promise<unknown> {
    this.invocations.push({ method, args });
    return undefined;
  }

  on(method: string, handler: HubHandler): void {
    this.handlers.set(method, handler);
  }

  onreconnecting(callback: (error?: Error) => void): void {
    this.onReconnecting = callback;
  }

  onreconnected(callback: (connectionId?: string) => void): void {
    this.onReconnected = callback;
  }

  onclose(callback: (error?: Error) => void): void {
    this.onClosed = callback;
  }

  /** Server-pushed event, delivered the way the hub would deliver it. */
  emit(method: string, payload: unknown): void {
    this.handlers.get(method)?.(payload);
  }

  dropConnection(): void {
    this.onReconnecting(new Error('transport closed'));
  }

  restoreConnection(): void {
    this.onReconnected('connection-2');
  }

  closeConnection(): void {
    this.onClosed(new Error('transport closed for good'));
  }

  /** Arguments of every call to `method`, in order. */
  callsTo(method: string): readonly string[] {
    return this.invocations
      .filter((invocation) => invocation.method === method)
      .map((invocation) => String(invocation.args[0]));
  }
}

export interface HubControl {
  readonly connections: FakeHubConnection[];
  readonly urls: string[];
  /** Injected into the next connection, so a hub that will not start is testable. */
  startError: Error | null;
  reset(): void;
  connection(): FakeHubConnection;
}

export const hubControl: HubControl = {
  connections: [],
  urls: [],
  startError: null,
  reset(): void {
    hubControl.connections.length = 0;
    hubControl.urls.length = 0;
    hubControl.startError = null;
  },
  connection(): FakeHubConnection {
    const latest = hubControl.connections[hubControl.connections.length - 1];
    if (latest === undefined) {
      throw new Error('The console never opened a hub connection.');
    }

    return latest;
  },
};

export class FakeHubConnectionBuilder {
  withUrl(url: string): this {
    hubControl.urls.push(url);
    return this;
  }

  withAutomaticReconnect(): this {
    return this;
  }

  configureLogging(): this {
    return this;
  }

  build(): FakeHubConnection {
    const connection = new FakeHubConnection();
    connection.startError = hubControl.startError;
    hubControl.connections.push(connection);
    return connection;
  }
}

/** The subset of `LogLevel` the client references. */
export const LogLevel = { Warning: 3 } as const;
