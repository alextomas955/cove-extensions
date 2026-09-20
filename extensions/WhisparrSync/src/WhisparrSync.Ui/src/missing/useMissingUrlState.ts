/**
 * The catalogue tab's view, kept in the page URL and shared by every control that reads it.
 *
 * Hand-built because neither the host's detail-tab URL hook nor its router location module is
 * exported to extensions.
 *
 * More than one component mounts this hook. `history.replaceState` fires no `popstate` and this
 * hook dispatches no host location event, so a write would otherwise reach only the instance that
 * made it. The subscriber set below carries one write to every reader.
 */
import { useCallback, useEffect, useState } from "react";

import { readMissingView, writeMissingView, type MissingView } from "./missingUrlLogic";

// The host event fired when its own router changes the address.
const HOST_LOCATION_CHANGE = "cove-locationchange";

// Module scope, because spanning component instances is the point. It holds callbacks and never a
// view, so an unmounted component leaves nothing behind.
const subscribers = new Set<(view: MissingView) => void>();

function notify(view: MissingView): void {
  for (const subscriber of subscribers) {
    subscriber(view);
  }
}

/**
 * The view the address describes, and a way to change it.
 *
 * The address is rewritten by replacement rather than pushed, so the back button leaves the tab
 * rather than stepping through half-typed searches. The host router reads none of these keys, so no
 * location event is dispatched: re-parsing the route would remount the tab for nothing.
 *
 * @returns the current view and a writer that reaches every mounted instance, the caller included.
 */
export function useMissingUrlState(): [MissingView, (view: MissingView) => void] {
  const [view, setView] = useState<MissingView>(() => readMissingView(window.location.search));

  useEffect(() => {
    const fromTheAddress = () => {
      notify(readMissingView(window.location.search));
    };

    subscribers.add(setView);
    window.addEventListener("popstate", fromTheAddress);
    window.addEventListener(HOST_LOCATION_CHANGE, fromTheAddress);
    return () => {
      subscribers.delete(setView);
      window.removeEventListener("popstate", fromTheAddress);
      window.removeEventListener(HOST_LOCATION_CHANGE, fromTheAddress);
    };
  }, []);

  const write = useCallback((next: MissingView) => {
    const search = writeMissingView(window.location.search, next);
    const address = `${window.location.pathname}${search === "" ? "" : `?${search}`}${window.location.hash}`;
    window.history.replaceState(window.history.state, "", address);

    // Read back rather than answering with what was asked for, so a navigation through the address
    // and a control's write deliver the same shape through the same path.
    notify(readMissingView(window.location.search));
  }, []);

  return [view, write];
}
