import { describe, expect, it } from 'vitest';

import { HiveApiError, PUBLIC_API_BASE_PATH, createHiveApiClient } from './client.js';
import type { FetchLike } from './client.js';
import type { OrganogramResponse, PositionStatesResponse } from './contracts.js';

interface RecordedRequest {
  url: string;
  headers: Record<string, string>;
}

function stubFetch(
  handler: (request: RecordedRequest) => Response,
): { fetch: FetchLike; requests: RecordedRequest[] } {
  const requests: RecordedRequest[] = [];
  const fetch: FetchLike = (input, init) => {
    const request = { url: input, headers: init?.headers ?? {} };
    requests.push(request);
    return Promise.resolve(handler(request));
  };
  return { fetch, requests };
}

function jsonResponse(body: unknown, init?: ResponseInit): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
    ...init,
  });
}

const organogram: OrganogramResponse = {
  registry: { version: 7, fingerprint: 'a1b2c3' },
  generated_at_utc: '2026-08-03T10:00:00+00:00',
  root_unit_id: 'delivery',
  organization: {
    id: 'acme-delivery',
    name: 'Acme Delivery',
    root_unit_id: 'delivery',
    root_position_id: 'head-of-delivery',
  },
  units: [],
  positions: [],
};

const states: PositionStatesResponse = {
  registry: { version: 7, fingerprint: 'a1b2c3' },
  generated_at_utc: '2026-08-03T10:00:00+00:00',
  last_event_applied_at_utc: '2026-08-03T09:59:58+00:00',
  states: [],
};

describe('createHiveApiClient', () => {
  it('reads the organogram from the public versioned surface with a bearer token', async () => {
    const { fetch, requests } = stubFetch(() => jsonResponse(organogram));
    const client = createHiveApiClient({
      baseUrl: 'https://hive.example.com/',
      token: 'secret',
      fetch,
    });

    await expect(client.getOrganogram('acme-delivery')).resolves.toEqual(organogram);
    expect(requests[0]?.url).toBe(
      `https://hive.example.com${PUBLIC_API_BASE_PATH}/organizations/acme-delivery/organogram`,
    );
    expect(requests[0]?.headers.authorization).toBe('Bearer secret');
  });

  it('resolves the token per request when a factory is supplied', async () => {
    let issued = 0;
    const { fetch, requests } = stubFetch(() => jsonResponse(organogram));
    const client = createHiveApiClient({
      baseUrl: 'https://hive.example.com',
      token: () => `rotated-${++issued}`,
      fetch,
    });

    await client.getOrganogram('acme-delivery');
    await client.getOrganogram('acme-delivery');

    expect(requests.map((request) => request.headers.authorization)).toEqual([
      'Bearer rotated-1',
      'Bearer rotated-2',
    ]);
  });

  it('escapes identifiers in unit and position routes', async () => {
    const { fetch, requests } = stubFetch(() => jsonResponse(organogram));
    const client = createHiveApiClient({
      baseUrl: 'https://hive.example.com',
      token: 'secret',
      fetch,
    });

    await client.getUnitOrganogram('acme delivery', 'unit/one');
    expect(requests[0]?.url).toBe(
      'https://hive.example.com/api/v1/organizations/acme%20delivery/units/unit%2Fone/organogram',
    );
  });

  it('returns the snapshot and ETag for a changed position-state poll', async () => {
    const { fetch } = stubFetch(() =>
      jsonResponse(states, { headers: { 'content-type': 'application/json', etag: 'W/"abc"' } }),
    );
    const client = createHiveApiClient({
      baseUrl: 'https://hive.example.com',
      token: 'secret',
      fetch,
    });

    const result = await client.getPositionStates('acme-delivery');

    expect(result).toEqual({ status: 'modified', etag: 'W/"abc"', snapshot: states });
  });

  it('sends If-None-Match and reports an unchanged snapshot without a body', async () => {
    const { fetch, requests } = stubFetch(() => new Response(null, { status: 304 }));
    const client = createHiveApiClient({
      baseUrl: 'https://hive.example.com',
      token: 'secret',
      fetch,
    });

    const result = await client.getPositionStates('acme-delivery', {
      ifNoneMatch: 'W/"abc"',
    });

    expect(requests[0]?.headers['if-none-match']).toBe('W/"abc"');
    expect(result).toEqual({ status: 'not-modified', etag: 'W/"abc"' });
  });

  it('surfaces Problem Details as a typed error', async () => {
    const { fetch } = stubFetch(() =>
      new Response(JSON.stringify({ title: 'Organization not found', status: 404 }), {
        status: 404,
        headers: { 'content-type': 'application/problem+json' },
      }),
    );
    const client = createHiveApiClient({
      baseUrl: 'https://hive.example.com',
      token: 'secret',
      fetch,
    });

    const error = await client
      .getPosition('acme-delivery', 'head-of-delivery')
      .catch((thrown: unknown) => thrown);

    expect(error).toBeInstanceOf(HiveApiError);
    const apiError = error as HiveApiError;
    expect(apiError.isNotFound).toBe(true);
    expect(apiError.problem?.title).toBe('Organization not found');
    expect(apiError.message).toBe('Organization not found (HTTP 404)');
  });

  it('reports an unavailable read model and an unknown credential distinctly', async () => {
    const { fetch } = stubFetch((request) =>
      request.url.includes('position-states')
        ? new Response(null, { status: 503 })
        : new Response(null, { status: 401 }),
    );
    const client = createHiveApiClient({
      baseUrl: 'https://hive.example.com',
      token: 'secret',
      fetch,
    });

    const unavailable = (await client
      .getPositionStates('acme-delivery')
      .catch((thrown: unknown) => thrown)) as HiveApiError;
    const unauthorized = (await client
      .getOrganogram('acme-delivery')
      .catch((thrown: unknown) => thrown)) as HiveApiError;

    expect(unavailable.isReadModelUnavailable).toBe(true);
    expect(unavailable.problem).toBeNull();
    expect(unauthorized.isUnauthorized).toBe(true);
  });

  it('refuses a base URL that targets the private internal surface', () => {
    expect(() =>
      createHiveApiClient({ baseUrl: 'https://hive.example.com/internal', token: 'secret' }),
    ).toThrow(/internal/);
  });
});
