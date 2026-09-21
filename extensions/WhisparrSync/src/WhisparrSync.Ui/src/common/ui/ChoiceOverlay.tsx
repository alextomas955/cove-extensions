/**
 * The panel a selection's handler opens: one row per thing it can do, and a way out that sends
 * nothing.
 *
 * It is mounted imperatively, because a selection-bar handler has no tree to render into.
 */
import { useRef, type ReactNode } from "react";
import { X } from "lucide-react";
import { useOverlayKeys } from "@cove-extensions/ui-shared/overlay";

import { selectionMenuHeader } from "./copy";
import { WhisparrLogo } from "./WhisparrLogo";

export type RowIcon = (props: { className?: string }) => ReactNode;

export interface ChoiceRow {
  readonly key: string;
  readonly label: string;
  readonly icon: RowIcon;
}

/**
 * The panel's width in pixels, applied inline like the placement below. The host's Tailwind JIT
 * never scans this bundle, so a width class it does not already emit would not render.
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
  count: number;
  /** Why nothing is offered, or null when something is. */
  reason: string | null;
  rows: readonly TRow[];
  cancelLabel: string;
  /** Used in place of the cancel label when there are no rows. */
  closeLabel: string;
  /** Called with the chosen row, or null when the reader leaves without choosing. */
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
      // A host action opens the panel, so there is no control to anchor it to. It is centred
      // against the viewport instead.
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

      {/* A menu role admits menu children only, so the way out carries one and the arrow keys
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
