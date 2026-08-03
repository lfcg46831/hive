import { useEffect, useState } from 'react';

/**
 * Ticking clock for age-based presentation.
 *
 * Staleness is a function of elapsed time, so a view that only re-renders on
 * server events would keep claiming to be fresh precisely when the events have
 * stopped. The clock runs only while `active`, which the console sets to «not
 * live»: with an established subscription there is no age to display.
 */
export function useNowMs(intervalMs: number, active: boolean): number {
  const [nowMs, setNowMs] = useState(() => Date.now());

  useEffect(() => {
    if (!active) {
      return undefined;
    }

    setNowMs(Date.now());
    const timer = setInterval(() => setNowMs(Date.now()), Math.max(500, intervalMs));
    return () => clearInterval(timer);
  }, [active, intervalMs]);

  return nowMs;
}
