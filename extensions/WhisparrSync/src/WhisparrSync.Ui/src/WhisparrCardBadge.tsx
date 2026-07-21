/**
 * The video-card-content slot: a per-scene status glyph + label in the card's content area, shown only while the
 * toolbar pill is on. Reads its scene from the top-level `video` prop (Cove's slot contract — never
 * `props.context.*`) and resolves status through cardStatusStore (one batched request per visible page). Renders
 * nothing when the pill is off or the scene has no status; the WhisparrLibraryRow pills key these glyphs.
 */
import { Ban, Bookmark, Circle, CircleDashed, Download } from "lucide-react";
import { useLibraryStatusOn } from "./libraryToggleStore";
import { useCardStatus } from "./cardStatusStore";
import { FILE_INDICATOR, SCENE_STATE_LABEL, type SceneWhisparrState } from "./sceneStatusLogic";

/** Each PRIMARY management state → its lucide glyph + host accent token (glyph + color, never color-only). */
const STATE_VISUAL: Record<SceneWhisparrState, { Icon: typeof Bookmark; color: string }> = {
  monitored: { Icon: Bookmark, color: "text-accent" },
  unmonitored: { Icon: Circle, color: "text-secondary" },
  notAdded: { Icon: CircleDashed, color: "text-muted" },
  excluded: { Icon: Ban, color: "text-red-400" },
};

export function WhisparrCardBadge(props: { video?: { id?: number } }) {
  const on = useLibraryStatusOn();
  const id = props.video?.id;
  const hasId = typeof id === "number";
  const status = useCardStatus(hasId ? id : -1, on && hasId);

  if (!on || status === null) {
    return null;
  }

  const visual = STATE_VISUAL[status.state];
  const StateIcon = visual.Icon;
  // Owns its strip chrome (divider + padding) — the host slot wrapper is chrome-less, so a card with no badge
  // shows nothing. The file dot is a SECONDARY signal painted after the primary state glyph; it never
  // replaces or recolors the primary glyph (a monitored+downloaded scene reads "Monitored" with a file dot).
  return (
    <span className="flex items-center gap-1 border-t border-border/50 px-2.5 py-1.5 text-xs text-secondary">
      <StateIcon
        className={`h-3.5 w-3.5 ${visual.color}`}
        fill={status.state === "monitored" ? "currentColor" : "none"}
      />
      {SCENE_STATE_LABEL[status.state]}
      {status.hasFile && (
        <Download
          className="ml-0.5 h-3 w-3 text-green-400"
          aria-label={FILE_INDICATOR.label}
          role="img"
        />
      )}
    </span>
  );
}
