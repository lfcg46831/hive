import { describe, expect, it } from 'vitest';
import type { InboxStatusInput } from './inboxStatus.js';
import { deriveInboxStatus } from './inboxStatus.js';

const NOW_MS = Date.parse('2026-08-09T12:00:00Z');

function input(overrides: Partial<InboxStatusInput> = {}): InboxStatusInput {
  return {
    phase: 'ready',
    error: null,
    loaded: true,
    itemCount: 0,
    channel: 'live',
    lastSyncedAtUtc: '2026-08-09T11:59:59Z',
    projectionAppliedAtUtc: '2026-08-09T11:59:58Z',
    pendingUpdate: false,
    missedNotifications: false,
    pollIntervalMs: 5_000,
    nowMs: NOW_MS,
    ...overrides,
  };
}

describe('deriveInboxStatus projection staleness', () => {
  it('does not present an empty response as current before any projection event is applied', () => {
    const status = deriveInboxStatus(input({ projectionAppliedAtUtc: null }));

    expect(status.stage).toBe('empty');
    expect(status.freshness.level).toBe('live');
    expect(status.notices.map((notice) => notice.id)).toContain('projection-not-started');
  });

  it('does not confuse an old event watermark with a stalled projection', () => {
    const status = deriveInboxStatus(
      input({ projectionAppliedAtUtc: '2026-08-09T11:59:40Z' }),
    );

    expect(status.freshness.level).toBe('live');
    expect(status.notices).toHaveLength(0);
  });

  it('preserves live transport freshness when the projection watermark is recent', () => {
    const status = deriveInboxStatus(input());

    expect(status.freshness.level).toBe('live');
    expect(status.notices).toHaveLength(0);
  });

  it('still reports transport staleness after successful updates stop', () => {
    const status = deriveInboxStatus(
      input({
        channel: 'polling',
        lastSyncedAtUtc: '2026-08-09T11:59:30Z',
      }),
    );

    expect(status.freshness.level).toBe('stale');
    expect(status.notices.map((notice) => notice.id)).toContain('stale');
  });
});
