/**
 * A read surface's four slots, chosen by the pure derivation beside it. A surface that can never
 * answer renders nothing, because an empty state there would read as a factual zero.
 */
import type { ReactNode } from "react";
import { Spinner } from "@cove-extensions/ui-shared";

import type { AsyncRegionState } from "./asyncRegionLogic";

export function AsyncRegion({
  state,
  available = true,
  outageNotice,
  // Defaulted here, not in the branch, so an omitted slot takes the spinner and an explicit null
  // renders nothing. A coalesce in the branch would give a caller asking for nothing a spinner.
  reading = <Spinner />,
  content,
  empty,
  failed,
}: {
  state: AsyncRegionState;
  /** `false` omits the surface from the DOM. A caller with no capability passes it. */
  available?: boolean;
  /** Rendered above kept content when a read failed behind it. */
  outageNotice?: ReactNode;
  /** Omit for the spinner; pass `null` for a surface that must draw nothing while it reads. */
  reading?: ReactNode;
  content: ReactNode;
  empty: ReactNode;
  failed: ReactNode;
}) {
  if (!available) {
    return null;
  }

  switch (state.status) {
    case "reading":
      return <>{reading}</>;
    case "content":
      return (
        <>
          {state.outage ? outageNotice : null}
          {content}
        </>
      );
    case "empty":
      return <>{empty}</>;
    case "failed":
      return <>{failed}</>;
  }
}
