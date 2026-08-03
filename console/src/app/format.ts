/** Presentation helpers shared by the console views. */

/**
 * Formats an API timestamp for display. Unparsable input is shown verbatim
 * rather than replaced by a placeholder: a console that hides malformed data
 * makes the malformed data harder to notice.
 */
export function formatUtc(timestamp: string | null): string {
  if (timestamp === null) {
    return '—';
  }

  const parsed = new Date(timestamp);
  if (Number.isNaN(parsed.getTime())) {
    return timestamp;
  }

  return parsed.toLocaleString(undefined, {
    dateStyle: 'medium',
    timeStyle: 'medium',
  });
}
