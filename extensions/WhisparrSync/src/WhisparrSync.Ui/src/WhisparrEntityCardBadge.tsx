/**
 * The studio/performer card-footer slot: a compact "Monitored · X/Y" line. Gated by the same toolbar pill as the
 * scene badge (libraryToggleStore) and shown only for a monitored entity. Reads its entity from the top-level
 * `studio`/`performer` slot prop (Cove's slot contract); status comes from entityCardStatusStore (one batched
 * request per kind per page). The host contains this slot, so it can't break the card. Studios ride both Whisparr
 * versions (v2 studio = site); performers are v3-only (v2 has no performer entity), so the v2 manifest omits them.
 */
import { Bookmark } from "lucide-react";
import { useLibraryStatusOn } from "./libraryToggleStore";
import { useEntityCardStatus } from "./entityCardStatusStore";

export function WhisparrEntityCardBadge(props: {
  studio?: { id?: number };
  performer?: { id?: number };
}) {
  const on = useLibraryStatusOn();
  const kind = props.studio ? "studio" : props.performer ? "performer" : null;
  const id = props.studio?.id ?? props.performer?.id;
  const hasId = typeof id === "number";
  const status = useEntityCardStatus(
    kind ?? "studio",
    hasId ? id : -1,
    on && kind !== null && hasId,
  );

  if (!on || !status?.monitored) {
    return null;
  }

  return (
    <span className="flex items-center gap-1.5 border-t border-border/50 px-2.5 py-1.5 text-xs text-secondary">
      <Bookmark className="h-3.5 w-3.5 text-accent" fill="currentColor" />
      Monitored
      {status.scenesTotal > 0 ? (
        <span
          className="text-muted"
          title={`${status.scenesPresent} of ${status.scenesTotal} scenes in Whisparr`}
        >
          · {status.scenesPresent}/{status.scenesTotal}
        </span>
      ) : null}
    </span>
  );
}
