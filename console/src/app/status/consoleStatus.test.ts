import { describe, expect, it } from 'vitest';
import { HiveApiError } from '../../api/index.js';
import type { OrganogramResponse } from '../../api/index.js';
import type { ConsoleStatusInput } from './consoleStatus.js';
import { deriveConsoleStatus, describeFailure, isEmptySnapshot } from './consoleStatus.js';

const NOW_MS = Date.parse('2026-08-03T12:00:00Z');

function snapshot(overrides: Partial<OrganogramResponse> = {}): OrganogramResponse {
  return {
    registry: { version: 7, fingerprint: 'fingerprint-7' },
    generated_at_utc: '2026-08-03T11:59:58Z',
    root_unit_id: 'unit-root',
    organization: {
      id: 'org-1',
      name: 'Org',
      root_unit_id: 'unit-root',
      root_position_id: 'position-root',
    },
    units: [
      {
        id: 'unit-root',
        name: 'Root',
        parent_unit_id: null,
        leadership_position_id: 'position-root',
      },
    ],
    positions: [
      {
        id: 'position-root',
        name: 'Lead',
        unit_id: 'unit-root',
        occupant: { id: 'agent-1', type: 'AiAgent' },
        hierarchy: { reports_to_position_id: null, direct_subordinate_position_ids: [] },
        operational_state: {
          position_id: 'position-root',
          state: 'Idle',
          sequence: 1,
          updated_at_utc: '2026-08-03T11:59:58Z',
          last_correlated_event: null,
        },
      },
    ],
    ...overrides,
  };
}

function input(overrides: Partial<ConsoleStatusInput> = {}): ConsoleStatusInput {
  return {
    phase: 'ready',
    error: null,
    snapshot: snapshot(),
    channel: 'live',
    lastSyncedAtUtc: '2026-08-03T11:59:58Z',
    registryUpdating: false,
    refreshing: false,
    pollIntervalMs: 5_000,
    nowMs: NOW_MS,
    ...overrides,
  };
}

function noticeIds(status: ReturnType<typeof deriveConsoleStatus>): readonly string[] {
  return status.notices.map((notice) => notice.id);
}

describe('deriveConsoleStatus stages', () => {
  it('is loading before the first snapshot arrives', () => {
    const status = deriveConsoleStatus(input({ phase: 'loading', snapshot: null, lastSyncedAtUtc: null }));

    expect(status.stage).toBe('loading');
    expect(status.failure).toBeNull();
    // A loading view has nothing to be degraded about yet.
    expect(status.notices).toHaveLength(0);
  });

  it('fails only when no snapshot was ever obtained', () => {
    const status = deriveConsoleStatus(
      input({ phase: 'failed', snapshot: null, lastSyncedAtUtc: null, error: new Error('offline') }),
    );

    expect(status.stage).toBe('failed');
    expect(status.failure?.retryable).toBe(true);
  });

  it('keeps the last known snapshot when a later load fails', () => {
    const status = deriveConsoleStatus(input({ phase: 'failed', error: new Error('offline') }));

    expect(status.stage).toBe('ready');
    expect(status.failure).toBeNull();
    expect(noticeIds(status)).toContain('update-failed');
  });

  it('separates an empty organization from a failed load', () => {
    const empty = snapshot({ units: [], positions: [] });

    expect(isEmptySnapshot(empty)).toBe(true);
    expect(deriveConsoleStatus(input({ snapshot: empty })).stage).toBe('empty');
  });

  it('does not call a snapshot empty when it holds positions without units', () => {
    expect(isEmptySnapshot(snapshot({ units: [] }))).toBe(false);
  });
});

describe('deriveConsoleStatus freshness', () => {
  it('claims live only with an established subscription', () => {
    const status = deriveConsoleStatus(input({ channel: 'live', nowMs: NOW_MS + 600_000 }));

    // Silence on a live hub means an unchanged organization, not a stalled view.
    expect(status.freshness.level).toBe('live');
  });

  it('is delayed while polling within the tolerated window', () => {
    const status = deriveConsoleStatus(input({ channel: 'polling' }));

    expect(status.freshness.level).toBe('delayed');
    expect(status.freshness.ageMs).toBe(2_000);
  });

  it('becomes stale after several missed poll intervals', () => {
    const status = deriveConsoleStatus(
      input({ channel: 'polling', pollIntervalMs: 20_000, nowMs: NOW_MS + 61_000 }),
    );

    expect(status.freshness.level).toBe('stale');
    expect(noticeIds(status)).toContain('stale');
  });

  it('applies a floor to the staleness threshold for fast poll intervals', () => {
    const withinFloor = deriveConsoleStatus(
      input({ channel: 'polling', pollIntervalMs: 1_000, nowMs: NOW_MS + 12_000 }),
    );
    const beyondFloor = deriveConsoleStatus(
      input({ channel: 'polling', pollIntervalMs: 1_000, nowMs: NOW_MS + 20_000 }),
    );

    expect(withinFloor.freshness.level).toBe('delayed');
    expect(beyondFloor.freshness.level).toBe('stale');
  });

  it('reports unknown freshness when nothing has synced or the timestamp is unusable', () => {
    expect(deriveConsoleStatus(input({ lastSyncedAtUtc: null })).freshness.level).toBe('unknown');
    expect(deriveConsoleStatus(input({ lastSyncedAtUtc: 'not-a-timestamp' })).freshness.level).toBe(
      'unknown',
    );
  });

  it('never reports a negative age when the server clock runs ahead', () => {
    const status = deriveConsoleStatus(input({ channel: 'polling', nowMs: NOW_MS - 30_000 }));

    expect(status.freshness.ageMs).toBe(0);
    expect(status.freshness.level).toBe('delayed');
  });
});

describe('deriveConsoleStatus notices', () => {
  it('names the connecting and reconnecting channels distinctly', () => {
    expect(noticeIds(deriveConsoleStatus(input({ channel: 'connecting' })))).toEqual(['connecting']);
    expect(noticeIds(deriveConsoleStatus(input({ channel: 'reconnecting' })))).toEqual([
      'reconnecting',
    ]);
  });

  it('states the polling interval in the fallback notice', () => {
    const status = deriveConsoleStatus(input({ channel: 'polling', pollIntervalMs: 5_000 }));

    expect(status.notices[0]?.id).toBe('polling');
    expect(status.notices[0]?.message).toContain('5s');
    expect(status.notices[0]?.retryable).toBe(true);
  });

  it('announces a registry update over the previous organogram', () => {
    const status = deriveConsoleStatus(input({ registryUpdating: true }));

    expect(noticeIds(status)).toEqual(['registry-updating']);
    expect(status.stage).toBe('ready');
  });

  it('reports an empty organization as degraded-free but still notices the channel', () => {
    const status = deriveConsoleStatus(
      input({ snapshot: snapshot({ units: [], positions: [] }), channel: 'polling' }),
    );

    expect(status.stage).toBe('empty');
    expect(noticeIds(status)).toContain('polling');
  });

  it('suppresses notices while the first load is still failing', () => {
    const status = deriveConsoleStatus(
      input({
        phase: 'failed',
        snapshot: null,
        channel: 'polling',
        registryUpdating: true,
        error: new Error('offline'),
      }),
    );

    // The failure panel already says everything; a stack of banners would not.
    expect(status.notices).toHaveLength(0);
  });
});

describe('describeFailure', () => {
  it('does not offer a retry for a rejected credential', () => {
    const failure = describeFailure(new HiveApiError(401, null, '/api/v1/organizations/org-1/organogram'));

    expect(failure.retryable).toBe(false);
    expect(failure.title).toContain('credential');
  });

  it('does not confirm existence for an out-of-scope organization', () => {
    const failure = describeFailure(new HiveApiError(404, null, '/api/v1/organizations/org-9/organogram'));

    expect(failure.retryable).toBe(false);
    expect(failure.detail).toContain('unknown or outside the scope');
  });

  it('treats an unmaterialized read model as self-resolving', () => {
    const failure = describeFailure(new HiveApiError(503, null, '/api/v1/organizations/org-1/organogram'));

    expect(failure.retryable).toBe(true);
    expect(failure.title).toContain('read model');
  });

  it('prefers the problem detail for other API failures', () => {
    const failure = describeFailure(
      new HiveApiError(
        500,
        { title: 'Unexpected', detail: 'Projection query failed.' },
        '/api/v1/organizations/org-1/organogram',
      ),
    );

    expect(failure.detail).toBe('Projection query failed.');
    expect(failure.retryable).toBe(true);
  });

  it('falls back to the transport error message', () => {
    expect(describeFailure(new Error('Failed to fetch')).detail).toBe('Failed to fetch');
  });
});
