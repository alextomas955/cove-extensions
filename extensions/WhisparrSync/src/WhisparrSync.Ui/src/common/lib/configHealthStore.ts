/**
 * Which required settings are unusable — a property of the CONNECTION, not of any entity, so one fetch of the
 * already-registered `/status` read is shared across every slot instead of one call per control. No route is
 * added for it.
 *
 * A failed fetch degrades to "nothing known to be unmet": a guard that refused because its own read failed
 * would refuse a working setup, which is worse than having no guard.
 */
import { useEffect, useSyncExternalStore } from "react";
import { request } from "./coveApi";
import { emit, newResourceEntry, runLoad } from "./resourceEntryLogic";
import { api } from "./extension";
import { registerConnectionScopedCache } from "./cacheRegistry";
import type { WhisparrConfigStatus } from "../../contracts";

export interface ConfigHealthState {
  /** The option keys the server reports as unusable for an action that creates a Whisparr record. */
  missingRequiredOptions: readonly string[];
  loading: boolean;
}

/** Frozen so `useSyncExternalStore` sees one identity until a fetch emits. */
const INITIAL: ConfigHealthState = Object.freeze({
  missingRequiredOptions: Object.freeze([]),
  loading: true,
});

const UNKNOWN: ConfigHealthState = Object.freeze({
  missingRequiredOptions: Object.freeze([]),
  loading: false,
});

// One entry, not a map: the fact is connection-global, so there is nothing to key it by. Its identity stays
// stable for the module's life so an invalidation cannot orphan an already-subscribed control.
const entry = newResourceEntry(INITIAL);

/**
 * Registered here rather than called from the settings slice, so a save invalidates this store without the
 * settings page importing it — the dependency stays slice → common. The generation bump makes an in-flight read
 * of the OLD connection discard its result.
 */
function clearConfigHealthCache(): void {
  entry.gen++;
  entry.inflight = null;
  if (entry.state !== INITIAL) {
    emit(entry, INITIAL);
  }
}

registerConnectionScopedCache(clearConfigHealthCache);

function load(): Promise<void> {
  return runLoad(
    entry,
    (prev) => (prev.loading ? prev : { ...prev, loading: true }),
    async () => {
      try {
        const resp = await request<WhisparrConfigStatus>(api("status"));
        return {
          missingRequiredOptions: Array.isArray(resp.missingRequiredOptions)
            ? resp.missingRequiredOptions
            : [],
          loading: false,
        };
      } catch {
        return UNKNOWN;
      }
    },
  );
}

/**
 * The shared hook every guarded control uses. The effect keys on `loading` (not `[]`) so a post-save
 * invalidation also refetches for controls that are ALREADY mounted; the in-flight guard keeps concurrent mounts
 * on one call.
 */
export function useConfigHealth(): ConfigHealthState {
  const state = useSyncExternalStore(
    (onChange) => {
      entry.listeners.add(onChange);
      return () => {
        entry.listeners.delete(onChange);
      };
    },
    () => entry.state,
    () => INITIAL,
  );

  useEffect(() => {
    if (entry.state.loading && !entry.inflight) {
      void load();
    }
  }, [state.loading]);

  return state;
}
