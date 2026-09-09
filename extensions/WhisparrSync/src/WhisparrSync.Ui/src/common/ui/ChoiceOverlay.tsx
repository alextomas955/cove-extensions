/**
 * The panel a selection's handler opens: one row per thing it can do over the whole selection, and a
 * way out that sends nothing.
 *
 * Presentational. The rows arrive already decided, so this module computes nothing about
 * capabilities, generations or bounds, and runs with no host and no network.
 *
 * Mounted imperatively rather than rendered into a tree, because a selection-bar handler owns none.
 */
import { useRef } from "react";
import { useOverlayKeys } from "@cove-extensions/ui-shared/overlay";

/** What one offered row needs to draw itself: a stable key, its own name, and what it states. */
export interface ChoiceRow {
  readonly key: string;
  readonly label: string;
  /** What is stated beneath it, in the order it reads. */
  readonly sentences: readonly string[];
}

/** Cove's own dialog framing, copied verbatim from the host so this sits where its dialogs do. */
const BACKDROP_CLASS = "fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4";

/** The host's own dialog panel. */
const PANEL_CLASS = "w-full max-w-md rounded-xl border border-border bg-card p-5 shadow-xl";

export function ChoiceOverlay<TRow extends ChoiceRow>({
  label,
  reason,
  rows,
  footer,
  cancelLabel,
  closeLabel,
  onChoose,
}: {
  /** The panel's accessible name, and what it asks when no reason displaces the question. */
  label: string;
  /** The one sentence saying why nothing is offered, or null when something is. */
  reason: string | null;
  /** The rows offered, in the order they read. Empty when there is nothing to offer. */
  rows: readonly TRow[];
  /** Where the result of a chosen row appears. Drawn only where a row can be chosen. */
  footer: string;
  /** The way out of a choice. */
  cancelLabel: string;
  /** The way out of a panel with nothing to choose between. */
  closeLabel: string;
  /** Called with the chosen row, or with null when the reader leaves without choosing. */
  onChoose: (row: TRow | null) => void;
}) {
  const panel = useRef<HTMLDivElement>(null);

  useOverlayKeys(panel, {
    onClose: () => {
      onChoose(null);
    },
    nav: "dialog",
  });

  return (
    <div className={BACKDROP_CLASS}>
      <div ref={panel} role="dialog" aria-modal="true" aria-label={label} className={PANEL_CLASS}>
        <p className="text-sm text-foreground">{reason ?? label}</p>

        {rows.map((row) => (
          <div key={row.key} className="mt-3">
            <button
              type="button"
              onClick={() => {
                onChoose(row);
              }}
              className="flex w-full items-center gap-2 text-left text-sm text-foreground"
            >
              {row.label}
            </button>
            {row.sentences.map((sentence) => (
              // Outside the button on purpose: text inside it would join the accessible name, and the
              // name a control announces has to be its own name, not a paragraph.
              <p key={sentence} className="mt-1 text-xs text-secondary">
                {sentence}
              </p>
            ))}
          </div>
        ))}

        {rows.length === 0 ? null : <p className="mt-3 text-xs text-secondary">{footer}</p>}

        <div className="mt-4 flex justify-end">
          <button
            type="button"
            onClick={() => {
              onChoose(null);
            }}
            className="rounded border border-border px-3 py-1.5 text-sm text-secondary hover:text-foreground"
          >
            {rows.length === 0 ? closeLabel : cancelLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
