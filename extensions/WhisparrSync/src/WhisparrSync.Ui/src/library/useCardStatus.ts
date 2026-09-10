/**
 * One card's status data layer: the only place a card badge reads what the instance holds.
 *
 * `enabled` gates the subscription, the snapshot and the registration together. With it off nothing
 * is registered, so no request is sent at all and a card is byte-identical to one with no extension
 * behind it.
 */
import { useCallback, useEffect, useSyncExternalStore } from "react";

import type { LibraryCardKind, LibraryCardReading } from "../wire/api";
import {
  cardStatusSettled,
  readCardStatus,
  requestCardStatus,
  subscribeCardStatus,
} from "./cardStatusStore";

/** What the hook hands the badge. */
export interface CardStatus {
  /** What the instance holds, or null where nothing was established for this card. */
  readonly reading: LibraryCardReading | null;
  /** Whether the read has answered. False while it is still in flight. */
  readonly settled: boolean;
}

export function useCardStatus(kind: LibraryCardKind, coveId: number, enabled: boolean): CardStatus {
  const subscribe = useCallback(
    (onChange: () => void) => (enabled ? subscribeCardStatus(onChange) : () => undefined),
    [enabled],
  );

  const reading = useSyncExternalStore(subscribe, () =>
    enabled ? readCardStatus(kind, coveId) : null,
  );
  const settled = useSyncExternalStore(subscribe, () =>
    enabled ? cardStatusSettled(kind, coveId) : false,
  );

  useEffect(() => {
    if (!enabled) return undefined;
    return requestCardStatus(kind, coveId);
  }, [enabled, kind, coveId]);

  return { reading, settled };
}
