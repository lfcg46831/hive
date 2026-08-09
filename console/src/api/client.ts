/**
 * Typed client for the public HIVE API.
 *
 * The client only ever calls `/api/v1` routes published in the OpenAPI
 * document; the private `/internal` surface is not reachable from here, and the
 * parity check asserts that every route template below exists in the document.
 */

import type {
  InboxDecisionRequest,
  InboxDecisionResponse,
  InboxDraftRequest,
  InboxEmissionError,
  InboxInteractionResponse,
  InboxItemResponse,
  InboxMessageType,
  InboxPage,
  InboxPriority,
  InboxReadState,
  InboxReplyRequest,
  InboxReplyResponse,
  InboxResponseState,
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
  '/api/v1/organizations/{organizationId}/inbox',
  '/api/v1/organizations/{organizationId}/positions/{positionId}/inbox',
  '/api/v1/organizations/{organizationId}/inbox/{itemId}',
  '/api/v1/organizations/{organizationId}/inbox/{itemId}/read',
  '/api/v1/organizations/{organizationId}/inbox/{itemId}/unread',
  '/api/v1/organizations/{organizationId}/inbox/{itemId}/draft',
  '/api/v1/organizations/{organizationId}/inbox/{itemId}/reply',
  '/api/v1/organizations/{organizationId}/inbox/{itemId}/decision',
] as const;

export type FetchLike = (
  input: string,
  init?: {
    method?: string;
    headers?: Record<string, string>;
    body?: string;
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

/** Server-side inbox filters. Everything the API cannot filter is not filtered. */
export interface InboxQuery {
  readonly type?: InboxMessageType;
  readonly readState?: InboxReadState;
  readonly responseState?: InboxResponseState;
  readonly priority?: InboxPriority;
  readonly deadlineFromUtc?: string;
  readonly deadlineToUtc?: string;
  readonly approvalPending?: boolean;
  readonly pageSize?: number;
  /** Opaque `next_cursor` of the previous page. */
  readonly cursor?: string;
}

export interface InboxRequestOptions extends RequestOptions {
  /** Last `ETag` observed, sent as `If-None-Match` for controlled polling. */
  readonly ifNoneMatch?: string | null;
}

/** A conditional inbox read, discriminated by server-side change. */
export type ConditionalResult<T> =
  | { readonly status: 'modified'; readonly etag: string | null; readonly snapshot: T }
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

  /**
   * Structured rejections from the governance validator, when the failure is a
   * rejected emission rather than a malformed request. The console reports them;
   * it never re-implements the rules that produced them.
   */
  get emissionErrors(): readonly InboxEmissionError[] {
    const errors = this.problem?.['errors'];
    return Array.isArray(errors) ? (errors as InboxEmissionError[]) : [];
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
  listInbox(
    organizationId: string,
    query?: InboxQuery,
    options?: InboxRequestOptions,
  ): Promise<ConditionalResult<InboxPage>>;
  listPositionInbox(
    organizationId: string,
    positionId: string,
    query?: InboxQuery,
    options?: InboxRequestOptions,
  ): Promise<ConditionalResult<InboxPage>>;
  getInboxItem(
    organizationId: string,
    itemId: string,
    options?: InboxRequestOptions,
  ): Promise<ConditionalResult<InboxItemResponse>>;
  /** Person-scoped read state. Emits no organizational message. */
  setInboxItemRead(
    organizationId: string,
    itemId: string,
    read: boolean,
    options?: RequestOptions,
  ): Promise<InboxInteractionResponse>;
  /**
   * Starts (`null`), replaces (text) or clears (`''`) the single draft this
   * principal holds for the item. Emits no organizational message.
   */
  saveInboxItemDraft(
    organizationId: string,
    itemId: string,
    body: string | null,
    options?: RequestOptions,
  ): Promise<InboxInteractionResponse>;
  /** Asks the occupied position to emit the correlated canonical response. */
  replyToInboxItem(
    organizationId: string,
    itemId: string,
    request: InboxReplyRequest,
    options?: RequestOptions,
  ): Promise<InboxReplyResponse>;
  /** Asks the occupied position to emit a correlated `ApprovalDecision`. */
  decideInboxApproval(
    organizationId: string,
    itemId: string,
    request: InboxDecisionRequest,
    options?: RequestOptions,
  ): Promise<InboxDecisionResponse>;
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
    body?: { readonly payload: unknown },
  ): Promise<{ response: Response; url: string }> {
    const url = `${baseUrl}${path}`;
    const response = await fetchImpl(url, {
      method: body === undefined ? 'GET' : 'POST',
      headers: {
        accept: 'application/json',
        authorization: await authorizationHeader(),
        ...(body === undefined ? {} : { 'content-type': 'application/json' }),
        ...headers,
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body.payload) }),
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

  async function postJson<T>(
    path: string,
    payload: unknown,
    options?: RequestOptions,
  ): Promise<T> {
    const { response, url } = await send(path, {}, options?.signal, { payload });
    if (!response.ok) {
      throw new HiveApiError(response.status, await readProblemDetails(response), url);
    }

    return (await response.json()) as T;
  }

  /** Shared shape of the three conditionally polled GET endpoints. */
  async function getConditional<T>(
    path: string,
    options?: InboxRequestOptions,
  ): Promise<ConditionalResult<T>> {
    const conditional: Record<string, string> = options?.ifNoneMatch
      ? { 'if-none-match': options.ifNoneMatch }
      : {};
    const { response, url } = await send(path, conditional, options?.signal);
    const etag = response.headers.get('etag');
    if (response.status === 304) {
      return { status: 'not-modified', etag: etag ?? options?.ifNoneMatch ?? null };
    }

    if (!response.ok) {
      throw new HiveApiError(response.status, await readProblemDetails(response), url);
    }

    return { status: 'modified', etag, snapshot: (await response.json()) as T };
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
    listInbox(organizationId, query, requestOptions) {
      return getConditional<InboxPage>(
        `${organizationPath(organizationId)}/inbox${inboxQueryString(query)}`,
        requestOptions,
      );
    },
    listPositionInbox(organizationId, positionId, query, requestOptions) {
      return getConditional<InboxPage>(
        `${organizationPath(organizationId)}/positions/${segment(positionId)}/inbox${inboxQueryString(
          query,
        )}`,
        requestOptions,
      );
    },
    getInboxItem(organizationId, itemId, requestOptions) {
      return getConditional<InboxItemResponse>(
        inboxItemPath(organizationId, itemId),
        requestOptions,
      );
    },
    setInboxItemRead(organizationId, itemId, read, requestOptions) {
      return postJson<InboxInteractionResponse>(
        `${inboxItemPath(organizationId, itemId)}/${read ? 'read' : 'unread'}`,
        // These actions carry no input; the API still expects a JSON body.
        {},
        requestOptions,
      );
    },
    saveInboxItemDraft(organizationId, itemId, body, requestOptions) {
      const request: InboxDraftRequest = { body };
      return postJson<InboxInteractionResponse>(
        `${inboxItemPath(organizationId, itemId)}/draft`,
        request,
        requestOptions,
      );
    },
    replyToInboxItem(organizationId, itemId, request, requestOptions) {
      return postJson<InboxReplyResponse>(
        `${inboxItemPath(organizationId, itemId)}/reply`,
        request,
        requestOptions,
      );
    },
    decideInboxApproval(organizationId, itemId, request, requestOptions) {
      return postJson<InboxDecisionResponse>(
        `${inboxItemPath(organizationId, itemId)}/decision`,
        request,
        requestOptions,
      );
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

  if (init?.body !== undefined) {
    requestInit.body = init.body;
  }

  return globalThis.fetch(input, requestInit);
};

function organizationPath(organizationId: string): string {
  return `${PUBLIC_API_BASE_PATH}/organizations/${segment(organizationId)}`;
}

function inboxItemPath(organizationId: string, itemId: string): string {
  return `${organizationPath(organizationId)}/inbox/${segment(itemId)}`;
}

/**
 * Serializes the server-side inbox filters. Undefined means «not filtered» and
 * is left off the wire entirely, so an unset filter never turns into a value the
 * API would have to interpret.
 */
function inboxQueryString(query: InboxQuery | undefined): string {
  if (query === undefined) {
    return '';
  }

  const parameters = new URLSearchParams();
  const append = (name: string, value: string | number | boolean | undefined): void => {
    if (value !== undefined) {
      parameters.append(name, String(value));
    }
  };

  append('type', query.type);
  append('read_state', query.readState);
  append('response_state', query.responseState);
  append('priority', query.priority);
  append('deadline_from_utc', query.deadlineFromUtc);
  append('deadline_to_utc', query.deadlineToUtc);
  append('approval_pending', query.approvalPending);
  append('page_size', query.pageSize);
  append('cursor', query.cursor);

  const serialized = parameters.toString();
  return serialized.length === 0 ? '' : `?${serialized}`;
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
