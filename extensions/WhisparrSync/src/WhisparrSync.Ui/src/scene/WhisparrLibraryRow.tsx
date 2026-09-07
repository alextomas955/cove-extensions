/**
 * The videos-list-row slot: the library-wide Whisparr status as tinted count pills on its own row below the
 * toolbar, shown only while the toolbar pill is on (libraryToggleStore). Doubles as the key for the per-card
 * glyphs (WhisparrCardBadge) — each pill pairs the same glyph with its count and label.
 */
import type { ReactNode } from "react";
import { Ban, Bookmark, Circle, CircleDashed, Download, Loader } from "lucide-react";
import { StatusPill, type StatusPillVariant } from "@cove-extensions/ui-shared";
import { useLibraryStatusOn } from "../common/lib/libraryToggleStore";
import { WHISPARR_UNAVAILABLE_COPY } from "../common/lib/whisparrCopy";
import { FILE_INDICATOR, SCENE_STATE_LABEL } from "./sceneStatusLogic";
import type { SceneWhisparrState } from "../contracts";
import { WhisparrLogo } from "../common/ui/WhisparrLogo";
import { useLibrarySummary } from "./sceneStatusStore";

/** Each PRIMARY state → glyph, glyph color, and StatusPill tint (actionable states tinted, the rest quiet). */
const STATE_PILL: Record<
  SceneWhisparrState,
  { Icon: typeof Bookmark; icon: string; variant: StatusPillVariant }
> = {
  monitored: { Icon: Bookmark, icon: "text-accent", variant: "accent" },
  unmonitored: { Icon: Circle, icon: "text-secondary", variant: "gray" },
  notAdded: { Icon: CircleDashed, icon: "text-muted", variant: "gray" },
  excluded: { Icon: Ban, icon: "text-muted", variant: "gray" },
};

/** The primary buckets shown as tinted pills, monitored-first. `notAdded` renders as the trailing muted count. */
const PILL_ORDER: readonly SceneWhisparrState[] = ["monitored", "unmonitored", "excluded"];

export function WhisparrLibraryRow() {
  const on = useLibraryStatusOn();
  const summary = useLibrarySummary(on);

  // Quiet when off, or when the connected Whisparr version has no per-scene status (v2): render nothing — an
  // unavailable capability is never surfaced. (The manifest omits the slot on v2; this covers the reload window
  // after a version switch too.)
  if (!on || summary.unsupported) {
    return null;
  }

  let body: ReactNode;
  if (summary.loading) {
    body = (
      <span className="inline-flex items-center gap-2 text-xs text-secondary">
        <Loader className="h-3 w-3 animate-spin" />
        Checking Whisparr…
      </span>
    );
  } else if (summary.error || summary.counts === null) {
    body = <span className="text-xs text-secondary">{WHISPARR_UNAVAILABLE_COPY}</span>;
  } else {
    const counts = summary.counts; // non-null here (the branch above handles null)
    body = (
      <span className="flex flex-1 flex-wrap items-center gap-2">
        {PILL_ORDER.map((state) => {
          const visual = STATE_PILL[state];
          const StateIcon = visual.Icon;
          return (
            <StatusPill
              key={state}
              variant={visual.variant}
              shape="tag"
              icon={
                <StateIcon
                  className={`h-3.5 w-3.5 ${visual.icon}`}
                  fill={state === "monitored" ? "currentColor" : "none"}
                />
              }
            >
              <span className="font-semibold tabular-nums text-foreground">{counts[state]}</span>
              <span className="text-secondary">{SCENE_STATE_LABEL[state]}</span>
            </StatusPill>
          );
        })}
        {/* The SECONDARY in-library count: a scene with a file, quietly tinted apart from the primary axis
            (it overlaps monitored/unmonitored, so it is not one of the management pills). */}
        <StatusPill
          variant="green"
          shape="tag"
          icon={<Download className="h-3.5 w-3.5 text-green-400" />}
        >
          <span className="font-semibold tabular-nums text-foreground">{counts.inLibrary}</span>
          <span className="text-secondary">{FILE_INDICATOR.label}</span>
        </StatusPill>
        <span className="ml-auto inline-flex items-center gap-1 text-xs text-muted">
          <span className="font-semibold tabular-nums">{counts.notAdded}</span>
          not added
        </span>
      </span>
    );
  }

  return (
    <div className="flex flex-wrap items-center gap-3 rounded-lg border border-border bg-card/80 px-3 py-2 mx-1 mt-1">
      <span className="inline-flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wide text-accent">
        <WhisparrLogo className="h-4 w-4" />
        Whisparr
      </span>
      {body}
    </div>
  );
}
