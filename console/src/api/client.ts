/**
 * Typed client for the public HIVE API.
 *
 * The client only ever calls `/api/v1` routes published in the OpenAPI
 * document; the private `/internal` surface is not reachable from here, and the
 * parity check asserts that every route template below exists in the document.
 */

import type {
  OrganogramResponse,
  PositionDetailResponse,
  PositionStatesResponse,
  ProblemDetails,
} from './contracts.js';

export const PUBLIC_API_BASE_PATH = '/api/v1';

/**
 * Route templates of the public organization surface, exactly as documented in
 * OpenAPI. Used by the parity check and by nothing else at runtime.
 */
export const PUBLIC_API_ROUTE_TEMPLATES = [
  '/api/v1/organizations/{organizationId}/organogram',
  '/api/v1/organizations/{organizationId}/units/{unitId}/organogram',
  '/api/v1/organizations/{organizationId}/positions/{positionId}',
  '/api/v1/organizations/{organizationId}/position-states',
] as const;

export type FetchLike = (
  input: string,
  init?: {
    method?: string;
    headers?: Record<string, string>;
    signal?: AbortSignal | undefined;
  },
) => Promise<Response>;

export interface HiveApiClientOptions {
  /** Origin of the API host, for example `https://hive.example.com`. */
  readonly baseUrl: string;
  /** Static organization bearer credential, or a factory resolving one. */
  readonly token: string | (() => string | Promise<string>);
  /** Injected for tests; defaults to the ambient `fetch`. */
  readonly fetch?: FetchLike;
}

export interface RequestOptions {
  readonly signal?: AbortSignal;
}

export interface PositionStatesRequestOptions extends RequestOptions {
  /** Last `ETag` observed, sent as `If-None-Match` for controlled polling. */
  readonly ifNoneMatch?: string | null;
}

/** Result of a position-state poll, discriminated by server-side change. */
export type PositionStatesResult =
  | { readonly status: 'modified'; readonly etag: string | null; readonly snapshot: PositionStatesResponse }
  | { readonly status: 'not-modified'; readonly etag: string | null };

/** Any non-success response from the public API. */
export class HiveApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemDetails | null,
    readonly url: string,
  ) {
    super(
      problem?.title
        ? `${problem.title} (HTTP ${status})`
        : `Public API request failed with HTTP ${status}`,
    );
    this.name = 'HiveApiError';
  }

  /** Unauthenticated or unknown credential. */
  get isUnauthorized(): boolean {
    return this.status === 401;
  }

  /**
   * Organization, unit or position not found. The API answers the same way for
   * resources outside the caller's scope, so this never confirms existence.
   */
  get isNotFound(): boolean {
    return this.status === 404;
  }

  /** Read model not materialized yet; the caller may retry. */
  get isReadModelUnavailable(): boolean {
    return this.status === 503;
  }
}

export interface HiveApiClient {
  readonly baseUrl: string;
  getOrganogram(organizationId: string, options?: RequestOptions): Promise<OrganogramResponse>;
  getUnitOrganogram(
    organizationId: string,
    unitId: string,
    options?: RequestOptions,
  ): Promise<OrganogramResponse>;
  getPosition(
    organizationId: string,
    positionId: string,
    options?: RequestOptions,
  ): Promise<PositionDetailResponse>;
  getPositionStates(
    organizationId: string,
    options?: PositionStatesRequestOptions,
  ): Promise<PositionStatesResult>;
}

export function createHiveApiClient(options: HiveApiClientOptions): HiveApiClient {
  const baseUrl = normalizeBaseUrl(options.baseUrl);
  const fetchImpl: FetchLike = options.fetch ?? ambientFetch;

  async function authorizationHeader(): Promise<string> {
    const token = typeof options.token === 'function' ? await options.token() : options.token;
    return `Bearer ${token}`;
  }

  async function send(
    path: string,
    headers: Record<string, string>,
    signal: AbortSignal | undefined,
  ): Promise<{ response: Response; url: string }> {
    const url = `${baseUrl}${path}`;
    const response = await fetchImpl(url, {
      method: 'GET',
      headers: { accept: 'application/json', authorization: await authorizationHeader(), ...headers },
      signal,
    });
    return { response, url };
  }

  async function getJson<T>(path: string, options?: RequestOptions): Promise<T> {
    const { response, url } = await send(path, {}, options?.signal);
    if (!response.ok) {
      throw new HiveApiError(response.status, await readProblemDetails(response), url);
    }

    return (await response.json()) as T;
  }

  return {
    baseUrl,
    getOrganogram(organizationId, requestOptions) {
      return getJson<OrganogramResponse>(
        `${organizationPath(organizationId)}/organogram`,
        requestOptions,
      );
    },
    getUnitOrganogram(organizationId, unitId, requestOptions) {
      return getJson<OrganogramResponse>(
        `${organizationPath(organizationId)}/units/${segment(unitId)}/organogram`,
        requestOptions,
      );
    },
    getPosition(organizationId, positionId, requestOptions) {
      return getJson<PositionDetailResponse>(
        `${organizationPath(organizationId)}/positions/${segment(positionId)}`,
        requestOptions,
      );
    },
    async getPositionStates(organizationId, requestOptions) {
      const conditional: Record<string, string> = requestOptions?.ifNoneMatch
        ? { 'if-none-match': requestOptions.ifNoneMatch }
        : {};
      const { response, url } = await send(
        `${organizationPath(organizationId)}/position-states`,
        conditional,
        requestOptions?.signal,
      );
      const etag = response.headers.get('etag');
      if (response.status === 304) {
        return { status: 'not-modified', etag: etag ?? requestOptions?.ifNoneMatch ?? null };
      }

      if (!response.ok) {
        throw new HiveApiError(response.status, await readProblemDetails(response), url);
      }

      return {
        status: 'modified',
        etag,
        snapshot: (await response.json()) as PositionStatesResponse,
      };
    },
  };
}

const ambientFetch: FetchLike = (input, init) => {
  const requestInit: RequestInit = { signal: init?.signal ?? null };
  if (init?.method !== undefined) {
    requestInit.method = init.method;
  }

  if (init?.headers !== undefined) {
    requestInit.headers = init.headers;
  }

  return globalThis.fetch(input, requestInit);
};

function organizationPath(organizationId: string): string {
  return `${PUBLIC_API_BASE_PATH}/organizations/${segment(organizationId)}`;
}

function segment(value: string): string {
  return encodeURIComponent(value);
}

function normalizeBaseUrl(baseUrl: string): string {
  const trimmed = baseUrl.trim().replace(/\/+$/, '');
  if (trimmed.length === 0) {
    throw new Error('baseUrl is required.');
  }

  if (trimmed.includes('/internal')) {
    throw new Error('The console must not target the private /internal surface.');
  }

  return trimmed;
}

async function readProblemDetails(response: Response): Promise<ProblemDetails | null> {
  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.includes('json')) {
    return null;
  }

  try {
    return (await response.json()) as ProblemDetails;
  } catch {
    return null;
  }
}
