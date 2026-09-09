/**
 * The panel a selection's handler opens: one row per thing it can do over the whole selection, and a
 * way out that sends nothing.
 *
 * Presentational. The rows arrive already decided, so this module computes nothing about
 * capabilities, generations or bounds, and runs with no host and no network.
 *
 * Mounted imperatively rather than rendered into a tree, because a selection-bar handler owns none.
 */
import { useRef, type ReactNode } from "react";
import { X } from "lucide-react";
import { useOverlayKeys } from "@cove-extensions/ui-shared/overlay";

import { selectionMenuHeader } from "./copy";
import { WhisparrLogo } from "./WhisparrLogo";

/** What draws a row's glyph. Every row carries one, so no row is a label on its own. */
export type RowIcon = (props: { className?: string }) => ReactNode;

/** What one offered row needs to draw itself: a stable key, its own name, and its glyph. */
export interface ChoiceRow {
  readonly key: string;
  readonly label: string;
  readonly icon: RowIcon;
}

/**
 * The panel's width in pixels.
 *
 * Inline rather than a class, as the placement below is: the host's Tailwind JIT never scans this
 * bundle, so a width class it does not already emit would not render.
 */
const PANEL_WIDTH = 288;

const ROW_CLASS =
  "flex w-full items-center gap-2 rounded-md px-3 py-2 text-left text-sm text-foreground transition-colors hover:bg-card focus:bg-card focus:outline-none";

export function ChoiceOverlay<TRow extends ChoiceRow>({
  count,
  reason,
  rows,
  cancelLabel,
  closeLabel,
  onChoose,
}: {
  /** How many things the selection holds, which the header names. */
  count: number;
  /** The one sentence saying why nothing is offered, or null when something is. */
  reason: string | null;
  /** The rows offered, in the order they read. Empty when there is nothing to offer. */
  rows: readonly TRow[];
  /** The way out of a choice. */
  cancelLabel: string;
  /** The way out of a panel with nothing to choose between. */
  closeLabel: string;
  /** Called with the chosen row, or with null when the reader leaves without choosing. */
  onChoose: (row: TRow | null) => void;
}) {
  const panel = useRef<HTMLDivElement>(null);
  const header = selectionMenuHeader(count);

  useOverlayKeys(panel, {
    onClose: () => {
      onChoose(null);
    },
    nav: "menu",
  });

  return (
    <div
      ref={panel}
      role="menu"
      aria-label={header}
      // The panel is opened from a host action rather than from a control this bundle owns, so there
      // is nothing to anchor it to and it is centred against the viewport instead.
      style={{
        position: "fixed",
        top: "20vh",
        left: "50%",
        transform: "translateX(-50%)",
        width: PANEL_WIDTH,
        zIndex: 60,
      }}
      className="flex flex-col gap-1 rounded-lg border border-border bg-background p-1 shadow-lg"
    >
      <div className="flex items-center gap-2 px-3 py-2">
        <WhisparrLogo className="h-4 w-4 text-secondary" />
        <span className="text-xs font-semibold uppercase tracking-wide text-secondary">
          {header}
        </span>
      </div>

      {reason === null ? null : <p className="px-3 py-2 text-xs text-secondary">{reason}</p>}

      {rows.map((row) => (
        <button
          key={row.key}
          type="button"
          role="menuitem"
          onClick={() => {
            onChoose(row);
          }}
          className={ROW_CLASS}
        >
          <row.icon className="h-4 w-4 text-secondary" />
          <span className="flex-1">{row.label}</span>
        </button>
      ))}

      {/* A menu role admits menu children only, so the way out carries one too and the arrow keys
          reach it. */}
      <button
        type="button"
        role="menuitem"
        onClick={() => {
          onChoose(null);
        }}
        className={ROW_CLASS}
      >
        <X className="h-4 w-4 text-secondary" />
        <span className="flex-1">{rows.length === 0 ? closeLabel : cancelLabel}</span>
      </button>
    </div>
  );
}
