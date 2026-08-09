import type {
  InboxMessageType,
  InboxPriority,
  InboxReadState,
  InboxResponseState,
} from '../../api/index.js';
import type { FilterSelection, InboxFilter } from './inboxFilter.js';
import { EMPTY_INBOX_FILTER, isInboxFilterActive } from './inboxFilter.js';
import { MESSAGE_TYPE_ORDER, PRIORITY_ORDER, messageTypeLabel, responseStateLabel } from './inboxItemView.js';

export interface InboxFilterBarProps {
  readonly filter: InboxFilter;
  readonly disabled: boolean;
  readonly onChange: (filter: InboxFilter) => void;
}

const READ_STATES: readonly InboxReadState[] = ['Unread', 'Read'];
const RESPONSE_STATES: readonly InboxResponseState[] = [
  'AwaitingResponse',
  'InProgress',
  'Responded',
  'NotApplicable',
];

/**
 * Inbox filters. Unlike the organogram's, these are queries: each change is a
 * new request, because the API owns ordering and pagination and a filter applied
 * to the page in hand would silently mean «of the first 25 items».
 *
 * No control here writes an organizational fact — narrowing a list is not an
 * action on the organization.
 */
export function InboxFilterBar({ filter, disabled, onChange }: InboxFilterBarProps) {
  const active = isInboxFilterActive(filter);

  return (
    <section className="filters" aria-label="Filter the inbox">
      <div className="filters__row filters__row--wrap">
        <Select
          label="Type"
          value={filter.type}
          disabled={disabled}
          options={MESSAGE_TYPE_ORDER.map((type) => ({ value: type, label: messageTypeLabel(type) }))}
          onChange={(type) => onChange({ ...filter, type: type as FilterSelection<InboxMessageType> })}
        />
        <Select
          label="Read state"
          value={filter.readState}
          disabled={disabled}
          options={READ_STATES.map((state) => ({ value: state, label: state }))}
          onChange={(readState) =>
            onChange({ ...filter, readState: readState as FilterSelection<InboxReadState> })
          }
        />
        <Select
          label="Response"
          value={filter.responseState}
          disabled={disabled}
          options={RESPONSE_STATES.map((state) => ({
            value: state,
            label: responseStateLabel(state),
          }))}
          onChange={(responseState) =>
            onChange({
              ...filter,
              responseState: responseState as FilterSelection<InboxResponseState>,
            })
          }
        />
        <Select
          label="Priority"
          value={filter.priority}
          disabled={disabled}
          options={PRIORITY_ORDER.map((priority) => ({ value: priority, label: priority }))}
          onChange={(priority) =>
            onChange({ ...filter, priority: priority as FilterSelection<InboxPriority> })
          }
        />

        <label className="filters__toggle">
          <input
            type="checkbox"
            className="filters__checkbox"
            checked={filter.approvalPending}
            disabled={disabled}
            onChange={(event) => onChange({ ...filter, approvalPending: event.target.checked })}
          />
          <span>Approval pending</span>
        </label>

        <button
          type="button"
          className="filters__clear"
          disabled={!active || disabled}
          onClick={() => onChange(EMPTY_INBOX_FILTER)}
        >
          Clear filters
        </button>
      </div>

      <p className="filters__result" role="status">
        {active
          ? 'Filters are applied by the API, so what is listed is the whole matching inbox, not a narrowed page.'
          : 'Showing every item assigned to your positions, ordered by deadline, priority and time.'}
      </p>
    </section>
  );
}

interface SelectProps {
  readonly label: string;
  readonly value: string;
  readonly disabled: boolean;
  readonly options: readonly { readonly value: string; readonly label: string }[];
  readonly onChange: (value: string) => void;
}

function Select({ label, value, disabled, options, onChange }: SelectProps) {
  return (
    <label className="filters__select">
      <span className="filters__label">{label}</span>
      <select
        className="filters__input"
        value={value}
        disabled={disabled}
        onChange={(event) => onChange(event.target.value)}
      >
        <option value="all">All</option>
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    </label>
  );
}
