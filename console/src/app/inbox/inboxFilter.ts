/**
 * The inbox filter, and its translation to the public query.
 *
 * Unlike the organogram, inbox filtering is server-side: the API owns ordering
 * and pagination, so a filter applied in the browser would narrow one page
 * rather than the inbox. Every field therefore maps to a documented query
 * parameter, and «all» means the parameter is left off the wire entirely.
 */

import type {
  InboxMessageType,
  InboxPriority,
  InboxQuery,
  InboxReadState,
  InboxResponseState,
} from '../../api/index.js';

export type FilterSelection<T> = T | 'all';

export interface InboxFilter {
  readonly type: FilterSelection<InboxMessageType>;
  readonly readState: FilterSelection<InboxReadState>;
  readonly responseState: FilterSelection<InboxResponseState>;
  readonly priority: FilterSelection<InboxPriority>;
  /** Narrows to items whose approval is still awaiting a decision. */
  readonly approvalPending: boolean;
}

export const EMPTY_INBOX_FILTER: InboxFilter = {
  type: 'all',
  readState: 'all',
  responseState: 'all',
  priority: 'all',
  approvalPending: false,
};

export function isInboxFilterActive(filter: InboxFilter): boolean {
  return (
    filter.type !== 'all' ||
    filter.readState !== 'all' ||
    filter.responseState !== 'all' ||
    filter.priority !== 'all' ||
    filter.approvalPending
  );
}

export function toInboxQuery(filter: InboxFilter, pageSize: number): InboxQuery {
  return {
    ...selected('type', filter.type),
    ...selected('readState', filter.readState),
    ...selected('responseState', filter.responseState),
    ...selected('priority', filter.priority),
    ...(filter.approvalPending ? { approvalPending: true } : {}),
    pageSize,
  };
}

function selected<TKey extends string, TValue extends string>(
  key: TKey,
  value: FilterSelection<TValue>,
): Partial<Record<TKey, TValue>> {
  return value === 'all' ? {} : ({ [key]: value } as Record<TKey, TValue>);
}
