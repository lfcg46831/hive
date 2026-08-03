/**
 * Runtime configuration of the console.
 *
 * The values are supplied by a `config.js` served next to `index.html`, not by
 * the bundle, so the same artifact can be deployed against any host without a
 * rebuild and no credential is ever compiled in. Resolution fails loudly: a
 * console pointed at nothing must not render an empty organogram that looks
 * like an organization with no units.
 */

export interface ConsoleConfig {
  /**
   * Origin of the public API host. When `config.js` leaves it empty the
   * console's own origin is used, which is what the dev-server proxy and a
   * co-hosted deployment both rely on.
   */
  readonly apiBaseUrl: string;
  readonly organizationId: string;
  /** Read-only organization bearer credential (US-F1-01-T07). */
  readonly token: string;
  /** Interval of the controlled `/position-states` polling fallback. */
  readonly pollIntervalMs: number;
}

export const DEFAULT_POLL_INTERVAL_MS = 5_000;
const MIN_POLL_INTERVAL_MS = 1_000;

export class ConsoleConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ConsoleConfigError';
  }
}

/** Resolves and validates the injected configuration, or throws. */
export function resolveConsoleConfig(source: unknown, fallbackBaseUrl: string): ConsoleConfig {
  if (source === null || typeof source !== 'object') {
    throw new ConsoleConfigError(
      'Console configuration is missing. Serve a config.js defining window.__HIVE_CONSOLE_CONFIG__ (see console/public/config.example.js).',
    );
  }

  const raw = source as Record<string, unknown>;
  const apiBaseUrl = optionalString(raw['apiBaseUrl']) ?? optionalString(fallbackBaseUrl);
  if (apiBaseUrl === undefined) {
    throw new ConsoleConfigError('Console configuration is missing "apiBaseUrl".');
  }

  if (apiBaseUrl.includes('/internal')) {
    throw new ConsoleConfigError('The console must not target the private /internal surface.');
  }

  return {
    apiBaseUrl,
    organizationId: requiredString(raw, 'organizationId'),
    token: requiredString(raw, 'token'),
    pollIntervalMs: resolvePollInterval(raw['pollIntervalMs']),
  };
}

/** Reads the configuration injected on the global object by `config.js`. */
export function readInjectedConsoleConfig(global: typeof globalThis = globalThis): ConsoleConfig {
  const scoped = global as { __HIVE_CONSOLE_CONFIG__?: unknown; location?: { origin?: string } };
  return resolveConsoleConfig(
    scoped.__HIVE_CONSOLE_CONFIG__ ?? null,
    scoped.location?.origin ?? '',
  );
}

function requiredString(raw: Record<string, unknown>, key: string): string {
  const value = optionalString(raw[key]);
  if (value === undefined) {
    throw new ConsoleConfigError(`Console configuration is missing "${key}".`);
  }

  return value;
}

function optionalString(value: unknown): string | undefined {
  if (typeof value !== 'string') {
    return undefined;
  }

  const trimmed = value.trim();
  return trimmed.length === 0 ? undefined : trimmed;
}

function resolvePollInterval(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return DEFAULT_POLL_INTERVAL_MS;
  }

  return Math.max(MIN_POLL_INTERVAL_MS, Math.trunc(value));
}
