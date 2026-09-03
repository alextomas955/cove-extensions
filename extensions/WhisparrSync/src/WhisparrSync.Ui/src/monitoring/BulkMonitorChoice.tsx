/**
 * The choice the selection bar's handler opens: one row per action the connected instance can carry
 * out over the whole selection, and a way out that sends nothing.
 *
 * Presentational. The rows arrive already decided, so this module computes nothing about
 * capabilities or scopes and runs with no host and no network.
 *
 * Mounted imperatively rather than rendered into a tree, because a bulk-action handler owns none.
 */
import { useRef } from "react";
import { useOverlayKeys } from "@cove-extensions/ui-shared/overlay";

import {
  BULK_CANCEL,
  BULK_CHOOSE_AN_ACTION,
  BULK_CLOSE,
  BULK_REPORTS_IN_THE_JOB_DRAWER,
} from "../common/ui/copy";
import type { BulkMonitorAction } from "./monitorMenuLogic";

/** Cove's own dialog framing, copied verbatim from the host so this sits where its dialogs do. */
const BACKDROP_CLASS = "fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4";

/** The host's own dialog panel. */
const PANEL_CLASS = "w-full max-w-md rounded-xl border border-border bg-card p-5 shadow-xl";

export function BulkMonitorChoice({
  actions,
  reason,
  onChoose,
}: {
  /** The actions offered, in the order they read. Empty when there is nothing to offer. */
  actions: readonly BulkMonitorAction[];
  /** The one sentence saying why nothing is offered, or null when something is. */
  reason: string | null;
  /** Called with the chosen action, or with null when the reader leaves without choosing. */
  onChoose: (action: BulkMonitorAction | null) => void;
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
      <div
        ref={panel}
        role="dialog"
        aria-modal="true"
        aria-label={BULK_CHOOSE_AN_ACTION}
        className={PANEL_CLASS}
      >
        <p className="text-sm text-foreground">{reason ?? BULK_CHOOSE_AN_ACTION}</p>

        {actions.map((action) => (
          <div key={action.key} className="mt-3">
            <button
              type="button"
              onClick={() => {
                onChoose(action);
              }}
              className="flex w-full items-center gap-2 text-left text-sm text-foreground"
            >
              {action.label}
            </button>
            {action.sentences.map((sentence) => (
              // Outside the button on purpose: text inside it would join the accessible name, and the
              // name a control announces has to be its own name, not a paragraph.
              <p key={sentence} className="mt-1 text-xs text-secondary">
                {sentence}
              </p>
            ))}
          </div>
        ))}

        {actions.length === 0 ? null : (
          <p className="mt-3 text-xs text-secondary">{BULK_REPORTS_IN_THE_JOB_DRAWER}</p>
        )}

        <div className="mt-4 flex justify-end">
          <button
            type="button"
            onClick={() => {
              onChoose(null);
            }}
            className="rounded border border-border px-3 py-1.5 text-sm text-secondary hover:text-foreground"
          >
            {actions.length === 0 ? BULK_CLOSE : BULK_CANCEL}
          </button>
        </div>
      </div>
    </div>
  );
}
