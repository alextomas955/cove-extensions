/**
 * Whether the library card badges are shown. One boolean, at module scope.
 *
 * Module scope is the deliberate exception to the per-entity store rule in
 * `monitoring/monitoringStore.ts`: the toolbar control and the card badges are separate host slot
 * instances and every one of them has to observe the same boolean.
 *
 * Nothing is persisted, so a reload starts the page off again.
 */
import { useSyncExternalStore } from "react";

let statusOn = false;
const listeners = new Set<() => void>();

/** Flips the badges on or off for every surface at once. */
export function toggleLibraryStatus(): void {
  statusOn = !statusOn;
  for (const listener of listeners) listener();
}

export function libraryStatusOn(): boolean {
  return statusOn;
}

export function subscribeLibraryStatus(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function useLibraryStatusOn(): boolean {
  return useSyncExternalStore(subscribeLibraryStatus, libraryStatusOn);
}
