/**
 * The studios/performers-list-row slot: the library-wide "N monitored in Whisparr" summary on its own row below
 * the toolbar, shown only while the toolbar pill is on (libraryToggleStore). One component serves both slots; it
 * reads the page from the top-level `pageKey` slot prop ("studios"/"performers", never `props.context.*`) to ask
 * {@link ./entityLibrarySummaryStore} for that kind's count. It keys the monitored bookmark the card footers draw.
 */
import type { ReactNode } from "react";
import { Bookmark, Loader } from "lucide-react";
import { useLibraryStatusOn } from "./libraryToggleStore";
import { WHISPARR_UNAVAILABLE_COPY } from "./monitorLogic";
import { useEntityLibrarySummary } from "./entityLibrarySummaryStore";
import { WhisparrLogo } from "./WhisparrLogo";

/** The list `pageKey` → the entity kind the summary endpoint expects, or null for a page we don't summarize. */
function kindOf(pageKey: string | undefined): "studio" | "performer" | null {
  if (pageKey === "studios") return "studio";
  if (pageKey === "performers") return "performer";
  return null;
}

export function WhisparrEntityLibraryRow(props: { pageKey?: string }) {
  const on = useLibraryStatusOn();
  const kind = kindOf(props.pageKey);
  const summary = useEntityLibrarySummary(kind ?? "studio", on && kind !== null);

  // Quiet when off, on a page this row doesn't summarize, or when the connected Whisparr version has no such
  // entity (a v2 performer): render nothing — an unavailable capability is never surfaced, not even to explain
  // it. (The manifest already omits the slot per version; this also covers the reload window after a switch.)
  if (!on || kind === null || summary.unsupported) {
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
  } else if (summary.unavailable) {
    body = <span className="text-xs text-secondary">{WHISPARR_UNAVAILABLE_COPY}</span>;
  } else {
    body = (
      <span className="flex flex-1 flex-wrap items-center gap-2">
        <span className="inline-flex items-center gap-1.5 rounded-md border border-accent/40 bg-accent/10 px-2 py-0.5 text-xs">
          <Bookmark className="h-3.5 w-3.5 text-accent" fill="currentColor" />
          <span className="font-semibold tabular-nums text-foreground">{summary.monitored}</span>
          <span className="text-secondary">Monitored</span>
        </span>
        <span className="ml-auto inline-flex items-center gap-1 text-xs text-muted">
          <span className="font-semibold tabular-nums">{summary.total - summary.monitored}</span>
          not monitored
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
