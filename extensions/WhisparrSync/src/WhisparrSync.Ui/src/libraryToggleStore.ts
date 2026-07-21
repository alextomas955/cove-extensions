/**
 * The on/off state of the library Whisparr-status affordance, shared across two host slots: the pill toggle in
 * the videos toolbar ({@link ./WhisparrLibraryToggle}) writes it, and the summary row below the toolbar
 * ({@link ./WhisparrLibraryRow}) reads it to reveal/hide itself. They are SEPARATE host-slot component
 * instances, so the toggle state cannot live in one component's `useState` — this module-level store is the
 * only thing that lets clicking the pill show/hide the row. Off by default (quiet by default).
 */
import { useSyncExternalStore } from "react";

let statusOn = false;
const listeners = new Set<() => void>();

function setStatusOn(next: boolean): void {
  if (statusOn === next) {
    return;
  }
  statusOn = next;
  listeners.forEach((listener) => {
    listener();
  });
}

/** Flips the library-status affordance; the pill toggle calls this. */
export function toggleLibraryStatus(): void {
  setStatusOn(!statusOn);
}

/** Subscribes a slot component to the shared on/off state. */
export function useLibraryStatusOn(): boolean {
  return useSyncExternalStore(
    (onChange) => {
      listeners.add(onChange);
      return () => listeners.delete(onChange);
    },
    () => statusOn,
    () => statusOn,
  );
}
