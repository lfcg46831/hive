// @vitest-environment jsdom

/**
 * Rendering tests of the organogram view (US-F1-01-T14).
 *
 * What is asserted here is what a reader can see: the hierarchy of units, who
 * holds each position, which position leads its unit, the operational state
 * being displayed, and the fact that a live state supersedes the one embedded in
 * the snapshot. Two properties get their own tests because breaking them makes
 * the console lie rather than merely look wrong: under a filter the view must
 * not state organizational facts that the filter itself is hiding, and nowhere
 * in this subtree may an affordance to edit the organization exist — structural
 * editing is F2 and the F1 console is strictly read-only.
 */

import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import type { OrganizationPositionState } from '../../api/index.js';
import {
  deliveryOrganization,
  position,
  positionState,
  snapshot,
  unit,
} from '../testing/organogramFixture.js';
import type { ConsoleFreshness } from '../status/consoleStatus.js';
import { OrganogramView } from './OrganogramView.js';
import type { OrganogramViewProps } from './OrganogramView.js';

afterEach(cleanup);

const LIVE_FRESHNESS: ConsoleFreshness = { level: 'live', label: 'Up to date', ageMs: 0 };

function renderView(overrides: Partial<OrganogramViewProps> = {}): HTMLElement {
  const props: OrganogramViewProps = {
    snapshot: deliveryOrganization(),
    liveStates: new Map<string, OrganizationPositionState>(),
    channel: 'live',
    freshness: LIVE_FRESHNESS,
    lastSyncedAtUtc: '2026-08-03T10:00:00.000Z',
    projectionAppliedAtUtc: null,
    registryUpdating: false,
    refreshing: false,
    ...overrides,
  };

  return render(<OrganogramView {...props} />).container;
}

function card(container: HTMLElement, positionId: string): HTMLElement {
  const element = container.querySelector(`[data-position-id="${positionId}"]`);
  expect(element, `position ${positionId} is not rendered`).not.toBeNull();
  return element as HTMLElement;
}

function unitNode(container: HTMLElement, unitId: string): HTMLElement | null {
  return container.querySelector(`[data-unit-id="${unitId}"]`);
}

function renderedStateOf(container: HTMLElement, positionId: string): string | null {
  return card(container, positionId).querySelector('[data-state]')?.getAttribute('data-state') ?? null;
}

describe('OrganogramView rendering', () => {
  it('nests every unit under its parent and keeps each position in its unit', () => {
    const container = renderView();

    const delivery = unitNode(container, 'delivery');
    const platform = unitNode(container, 'platform');
    const runtime = unitNode(container, 'runtime');

    expect(delivery?.getAttribute('data-depth')).toBe('0');
    expect(platform?.getAttribute('data-depth')).toBe('1');
    expect(runtime?.getAttribute('data-depth')).toBe('2');
    expect(delivery?.contains(platform)).toBe(true);
    expect(platform?.contains(runtime)).toBe(true);
    expect(runtime?.contains(card(container, 'runtime-engineer'))).toBe(true);
  });

  it('shows the occupant, the reporting line and the subordinate count of a position', () => {
    const container = renderView();
    const head = within(card(container, 'head-of-delivery'));

    expect(head.getByText('Head of Delivery')).toBeDefined();
    expect(head.getByText('ana.sousa')).toBeDefined();
    expect(head.getByText('human')).toBeDefined();
    expect(head.getByText('Unit leadership')).toBeDefined();

    const lead = within(card(container, 'platform-lead'));
    expect(lead.getByText('head-of-delivery')).toBeDefined();
    expect(lead.getByText('AI agent')).toBeDefined();
  });

  it('names a vacant position as vacant instead of leaving the occupant blank', () => {
    const container = renderView();

    expect(within(card(container, 'runtime-engineer')).getByText('Vacant')).toBeDefined();
  });

  it('renders the operational state the API resolved, without re-deriving it', () => {
    const container = renderView();

    expect(renderedStateOf(container, 'head-of-delivery')).toBe('WaitingHuman');
    expect(renderedStateOf(container, 'platform-lead')).toBe('Working');
    expect(renderedStateOf(container, 'runtime-lead')).toBe('Blocked');
    expect(within(card(container, 'head-of-delivery')).getByText('Waiting for human')).toBeDefined();
  });

  it('prefers a live state over the one embedded in the snapshot', () => {
    const container = renderView({
      liveStates: new Map([
        ['runtime-lead', positionState({ positionId: 'runtime-lead', state: 'Idle', sequence: 12 })],
      ]),
    });

    expect(renderedStateOf(container, 'runtime-lead')).toBe('Idle');
    // Positions without a live update keep exactly what the snapshot said.
    expect(renderedStateOf(container, 'platform-lead')).toBe('Working');
  });

  it('reports the last correlated event of a position, or its absence', () => {
    const container = renderView();

    const head = within(card(container, 'head-of-delivery'));
    expect(head.getByText('ApprovalRequest')).toBeDefined();
    expect(head.getByText('thread thread-9')).toBeDefined();
    expect(head.getByText(/sequence 4/)).toBeDefined();

    expect(within(card(container, 'platform-lead')).getByText('—')).toBeDefined();
  });

  it('names how the view is being kept current and which registry it is showing', () => {
    const container = renderView({
      channel: 'polling',
      freshness: { level: 'delayed', label: 'Updated 4s ago', ageMs: 4_000 },
    });

    const indicator = container.querySelector('.update-indicator') as HTMLElement;
    expect(indicator.getAttribute('data-channel')).toBe('polling');
    expect(indicator.getAttribute('data-freshness')).toBe('delayed');
    expect(within(indicator).getByText('Polling fallback')).toBeDefined();
    expect(within(indicator).getByText('Updated 4s ago')).toBeDefined();
    expect(within(indicator).getByText(/Registry v7/)).toBeDefined();
  });

  it('says a registry version is being replaced while the refetch is in flight', () => {
    renderView({ registryUpdating: true, refreshing: true });

    expect(screen.getByText(/Registry v7 · updating/)).toBeDefined();
    expect(screen.getByText('Refreshing…')).toBeDefined();
  });
});

describe('OrganogramView on snapshots that are not an organogram', () => {
  it('states that the organization is empty instead of rendering an empty tree', () => {
    const container = renderView({ snapshot: snapshot() });

    expect(screen.getByText('This organization has no units or positions')).toBeDefined();
    expect(screen.getByText(/at registry v7/)).toBeDefined();
    // Nothing to filter, so the controls that would suggest hidden data are gone.
    expect(container.querySelector('.filters')).toBeNull();
  });

  it('keeps units caught in a cycle and positions without a unit visible', () => {
    const container = renderView({
      snapshot: snapshot({
        units: [
          unit('delivery', null, 'head-of-delivery', 'Delivery'),
          unit('a', 'b', 'a-lead', 'Unit A'),
          unit('b', 'a', 'b-lead', 'Unit B'),
        ],
        positions: [
          position({ id: 'head-of-delivery', unitId: 'delivery' }),
          position({ id: 'a-lead', unitId: 'a' }),
          position({ id: 'ghost', unitId: 'vanished' }),
        ],
      }),
    });

    const detached = within(screen.getByLabelText('Detached units'));
    expect(detached.getByText('Unit A')).toBeDefined();

    const orphans = within(screen.getByLabelText('Positions without a unit'));
    expect(orphans.getByText('ghost')).toBeDefined();
    expect(container.querySelectorAll('[data-position-id="ghost"]')).toHaveLength(1);
  });

  it('reports leadership that names no position of the unit', () => {
    renderView({
      snapshot: snapshot({
        units: [unit('delivery', null, 'elsewhere', 'Delivery')],
        positions: [position({ id: 'member', unitId: 'delivery' })],
      }),
    });

    expect(screen.getByText(/Declared leadership/)).toBeDefined();
  });
});

describe('OrganogramView filtering', () => {
  function search(term: string): void {
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: term } });
  }

  it('narrows to the matching position while keeping it under its ancestors', async () => {
    const container = renderView();

    search('runtime engineer');

    await waitFor(() => {
      expect(container.querySelector('[data-position-id="platform-lead"]')).toBeNull();
    });
    expect(card(container, 'runtime-engineer')).toBeDefined();
    expect(unitNode(container, 'delivery')).not.toBeNull();
    expect(unitNode(container, 'platform')).not.toBeNull();
    expect(screen.getByText(/Showing 1 of 4 positions/)).toBeDefined();
  });

  it('does not claim a unit has no positions when the filter hid them', async () => {
    const container = renderView();

    search('runtime engineer');

    await waitFor(() => {
      expect(within(unitNode(container, 'delivery')!).getAllByText(
        'No position of this unit matches the filters.',
      ).length).toBeGreaterThan(0);
    });
    expect(screen.queryByText('No positions in this unit.')).toBeNull();
  });

  it('matches occupants by identity and by kind', async () => {
    const container = renderView();

    search('sousa');

    await waitFor(() => expect(card(container, 'head-of-delivery')).toBeDefined());
    expect(container.querySelector('[data-position-id="runtime-lead"]')).toBeNull();

    search('vacant');

    await waitFor(() => expect(card(container, 'runtime-engineer')).toBeDefined());
    expect(container.querySelector('[data-position-id="head-of-delivery"]')).toBeNull();
  });

  it('filters by the state the reader sees, live updates included', async () => {
    const container = renderView({
      liveStates: new Map([
        positionEntry(positionState({ positionId: 'runtime-engineer', state: 'Blocked', sequence: 9 })),
      ]),
    });

    fireEvent.click(screen.getByRole('checkbox', { name: /Blocked/ }));

    await waitFor(() => expect(card(container, 'runtime-engineer')).toBeDefined());
    expect(card(container, 'runtime-lead')).toBeDefined();
    expect(container.querySelector('[data-position-id="platform-lead"]')).toBeNull();
  });

  it('counts states over the unfiltered snapshot so an empty bucket stays visible', async () => {
    const container = renderView();
    const offline = container.querySelector('.filters__state[data-state="Offline"]');

    expect(offline?.querySelector('.filters__count')?.textContent).toBe('0');

    search('sousa');
    await waitFor(() => expect(screen.getByText(/Showing 1 of 4 positions/)).toBeDefined());
    expect(offline?.querySelector('.filters__count')?.textContent).toBe('0');
    expect(
      container.querySelector('.filters__state[data-state="Working"] .filters__count')?.textContent,
    ).toBe('1');
  });

  it('restores the whole organogram when the filters are cleared', async () => {
    const container = renderView();
    const clear = screen.getByRole('button', { name: 'Clear filters' });
    expect((clear as HTMLButtonElement).disabled).toBe(true);

    search('sousa');
    await waitFor(() => expect((clear as HTMLButtonElement).disabled).toBe(false));

    fireEvent.click(clear);

    await waitFor(() => expect(card(container, 'runtime-engineer')).toBeDefined());
    expect(screen.getByText(/Showing all 4 positions/)).toBeDefined();
  });

  it('says nothing matched instead of looking like an empty organization', async () => {
    const container = renderView();

    search('nobody-by-this-name');

    await waitFor(() =>
      expect(screen.getByText(/No position matches the current filters/)).toBeDefined(),
    );
    expect(container.querySelectorAll('[data-position-id]')).toHaveLength(0);
    // The empty-organization panel would be a different, and false, claim.
    expect(screen.queryByText('This organization has no units or positions')).toBeNull();
  });
});

describe('OrganogramView read-only guarantee', () => {
  it('offers no control other than narrowing what is displayed', () => {
    const container = renderView();

    const buttons = [...container.querySelectorAll('button')].map((button) => button.textContent);
    expect(buttons).toEqual(['Clear filters']);

    const inputs = [...container.querySelectorAll('input')].map((input) => input.type);
    expect(inputs).toEqual(['search', 'checkbox', 'checkbox', 'checkbox', 'checkbox', 'checkbox']);

    expect(container.querySelector('form')).toBeNull();
    expect(container.querySelector('textarea')).toBeNull();
    expect(container.querySelector('select')).toBeNull();
    expect(container.querySelector('[contenteditable]')).toBeNull();
    expect(container.querySelectorAll('a[href]')).toHaveLength(0);
  });

  it('exposes no wording that suggests the organization can be changed here', () => {
    const container = renderView();

    expect(container.textContent ?? '').not.toMatch(
      /\b(edit|editar|delete|remove|create|add|save|assign|reassign|move|rename)\b/i,
    );
  });
});

function positionEntry(state: OrganizationPositionState): [string, OrganizationPositionState] {
  return [state.position_id, state];
}
