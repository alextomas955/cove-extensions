import { useEffect, useState } from "react";

/** Under the one-minute resolution the ages are rendered at, so an age is never visibly stale. */
const TICK_MS = 30_000;

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
