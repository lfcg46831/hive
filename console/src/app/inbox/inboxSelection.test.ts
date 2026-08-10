import { describe, expect, it } from 'vitest';
import type { InboxItem } from '../../api/index.js';
import { indexOfSelection, resolveInboxSelection } from './inboxSelection.js';

function items(...ids: readonly string[]): readonly InboxItem[] {
  return ids.map((id) => ({ item_id: id }) as InboxItem);
}

describe('resolveInboxSelection', () => {
  it('selects the first item when nothing is selected yet', () => {
    expect(
      resolveInboxSelection({ items: items('a', 'b', 'c'), selectedItemId: null, lastIndex: 0 }),
    ).toBe('a');
  });

  it('keeps the selection when the item is still in the list', () => {
    expect(
      resolveInboxSelection({ items: items('a', 'b', 'c'), selectedItemId: 'b', lastIndex: 1 }),
    ).toBe('b');
  });

  it('keeps the selection even when the item moved in the order', () => {
    expect(
      resolveInboxSelection({ items: items('c', 'b', 'a'), selectedItemId: 'b', lastIndex: 0 }),
    ).toBe('b');
  });

  it('succeeds to the item that took the place of one that left the list', () => {
    // 'b' was read under an unread filter and dropped out; 'c' is now second.
    expect(
      resolveInboxSelection({ items: items('a', 'c', 'd'), selectedItemId: 'b', lastIndex: 1 }),
    ).toBe('c');
  });

  it('succeeds to the last item when the one that left was last', () => {
    expect(
      resolveInboxSelection({ items: items('a', 'b'), selectedItemId: 'c', lastIndex: 2 }),
    ).toBe('b');
  });

  it('does not fall back to the top when the list only shrank', () => {
    expect(
      resolveInboxSelection({ items: items('a', 'b', 'c'), selectedItemId: 'z', lastIndex: 2 }),
    ).toBe('c');
  });

  it('has no selection for an empty list', () => {
    expect(resolveInboxSelection({ items: [], selectedItemId: 'a', lastIndex: 0 })).toBeNull();
    expect(resolveInboxSelection({ items: [], selectedItemId: null, lastIndex: 0 })).toBeNull();
  });
});

describe('indexOfSelection', () => {
  it('reports where the selection sits', () => {
    expect(indexOfSelection(items('a', 'b', 'c'), 'c')).toBe(2);
  });

  it('reports -1 for no selection and for one outside the list', () => {
    expect(indexOfSelection(items('a'), null)).toBe(-1);
    expect(indexOfSelection(items('a'), 'z')).toBe(-1);
  });
});
