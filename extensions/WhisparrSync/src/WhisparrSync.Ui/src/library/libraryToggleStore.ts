/**
 * Whether the library card badges are shown. One boolean, at module scope.
 *
 * Module scope is the deliberate exception to the per-entity store rule stated in
 * `monitoring/monitoringStore.ts`: each list toolbar renders its own component instance and every
 * one of them has to observe the same boolean, so a store created per component lifetime could not
 * carry it.
 *
 * Nothing is persisted. A reload starts the page off again, and nothing of this extension's is
 * written into the host page's browser storage.
 */
import { useSyncExternalStore } from "react";

let statusOn = false;
const listeners = new Set<() => void>();

/** Flips the badges on or off for every surface at once. */
export function toggleLibraryStatus(): void {
  statusOn = !statusOn;
  for (const listener of listeners) listener();
}

/** Subscribes a component to the shared boolean. */
export function useLibraryStatusOn(): boolean {
  return useSyncExternalStore(
    (onChange) => {
      listeners.add(onChange);
      return () => listeners.delete(onChange);
    },
    () => statusOn,
  );
}
