/**
 * Which item the inbox shows in the detail panel.
 *
 * The selection is an invariant of the view, not something the reader has to
 * restore: while the visible list has items, exactly one of them is selected.
 * Two rules resolve it against every new list — continuity, so a background
 * update never pulls the reader out of the item being read, and succession, so
 * an item that leaves the list hands over to whatever now occupies its place in
 * the order the API fixed, never back to the top.
 */

import type { InboxItem } from '../../api/index.js';

export interface InboxSelectionInput {
  readonly items: readonly InboxItem[];
  readonly selectedItemId: string | null;
  /**
   * Index the current selection held in the list the reader last saw. It is the
   * only reason succession can mean "the next one" instead of "the first one":
   * the vanished item is no longer there to be found.
   */
  readonly lastIndex: number;
}

/** The item that must be selected for the given list, or null when it is empty. */
export function resolveInboxSelection({
  items,
  selectedItemId,
  lastIndex,
}: InboxSelectionInput): string | null {
  if (items.length === 0) {
    return null;
  }

  if (selectedItemId !== null && items.some((item) => item.item_id === selectedItemId)) {
    return selectedItemId;
  }

  // No selection yet means the first item; a selection that is gone means the
  // item that took its place, clamped when it was the last one.
  const index = Math.min(Math.max(lastIndex, 0), items.length - 1);
  return items[index]?.item_id ?? null;
}

/** Where the selection sits now, or −1 when it is not in this list. */
export function indexOfSelection(
  items: readonly InboxItem[],
  selectedItemId: string | null,
): number {
  if (selectedItemId === null) {
    return -1;
  }

  return items.findIndex((item) => item.item_id === selectedItemId);
}
