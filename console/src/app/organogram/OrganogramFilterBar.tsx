import type { PositionOperationalState } from '../../api/index.js';
import { PositionStateBadge } from './PositionStateBadge.js';
import type { OrganogramFilter, OrganogramFilterResult } from './organogramFilter.js';
import { FILTERABLE_STATES, EMPTY_FILTER, isFilterActive } from './organogramFilter.js';

export interface OrganogramFilterBarProps {
  readonly filter: OrganogramFilter;
  readonly result: OrganogramFilterResult;
  readonly onChange: (filter: OrganogramFilter) => void;
}

/**
 * The only controls of the console that are not a re-read.
 *
 * They narrow what is displayed and nothing else: no request is issued, no
 * organizational fact is written, and no affordance here implies that the
 * structure can be edited — structural editing is F2. Counts come from the
 * unfiltered snapshot, so a state that no position is in reads as an empty
 * bucket instead of disappearing and leaving the reader to guess whether the
 * filter or the organization is empty.
 */
export function OrganogramFilterBar({ filter, result, onChange }: OrganogramFilterBarProps) {
  const active = isFilterActive(filter);

  function toggleState(state: PositionOperationalState): void {
    const states = new Set(filter.states);
    if (!states.delete(state)) {
      states.add(state);
    }

    onChange({ ...filter, states });
  }

  return (
    <section className="filters" aria-label="Filter the organogram">
      <div className="filters__row">
        <label className="filters__search">
          <span className="filters__label">Search</span>
          <input
            type="search"
            className="filters__input"
            value={filter.query}
            placeholder="Unit, position or occupant"
            autoComplete="off"
            onChange={(event) => onChange({ ...filter, query: event.target.value })}
          />
        </label>

        <button
          type="button"
          className="filters__clear"
          disabled={!active}
          onClick={() => onChange(EMPTY_FILTER)}
        >
          Clear filters
        </button>
      </div>

      <div className="filters__states" role="group" aria-label="Filter by operational state">
        {FILTERABLE_STATES.map((state) => {
          const selected = filter.states.has(state);
          const count = result.stateCounts.get(state) ?? 0;

          return (
            <label
              key={state}
              className={`filters__state${selected ? ' filters__state--selected' : ''}`}
              data-state={state}
            >
              <input
                type="checkbox"
                className="filters__checkbox"
                checked={selected}
                onChange={() => toggleState(state)}
              />
              <PositionStateBadge state={state} />
              <span className="filters__count">{count}</span>
            </label>
          );
        })}
      </div>

      <p className="filters__result" role="status">
        {describeResult(result)}
      </p>
    </section>
  );
}

function describeResult(result: OrganogramFilterResult): string {
  if (!result.active) {
    return `Showing all ${result.totalPositions} positions.`;
  }

  if (result.matchedPositions === 0) {
    return 'No position matches the current filters. Units and positions outside the match are hidden, not removed.';
  }

  return `Showing ${result.matchedPositions} of ${result.totalPositions} positions. Units without a match are kept only where they lead to one.`;
}
