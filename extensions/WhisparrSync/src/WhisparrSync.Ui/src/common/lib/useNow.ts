import { useEffect, useState } from "react";

/**
 * How often the instant is re-read. A relative age is only ever this far behind, which is under the
 * one-minute resolution the ages themselves are rendered at.
 */
const TICK_MS = 30_000;

/**
 * The current instant, re-read on an interval.
 *
 * A clock read during render is not idempotent, and a clock read once is one a page left open for an
 * hour keeps reporting: "just now" stays on screen long after it stopped being true.
 */
export function useNow(): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const timer = setInterval(() => {
      setNow(Date.now());
    }, TICK_MS);
    return () => {
      clearInterval(timer);
    };
  }, []);

  return now;
}
