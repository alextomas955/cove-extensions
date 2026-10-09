/**
 * The settings save bar: fixed to the bottom of the page, shown only while something is unsaved or
 * the last save is still being reported.
 *
 * The bar states what a page holds and what the last save did; the page supplies both. Each state
 * carries its own sentence beside the dot, so nothing here is signalled by colour alone.
 */
import type { ReactNode } from "react";

import { Button, Spinner, StatusText } from "./primitives";

/** What the last save did, and what the bar says about it. */
export type SaveOutcome =
  | { readonly kind: "none" }
  | { readonly kind: "saved"; readonly message: string }
  | { readonly kind: "failed"; readonly message: string };

export interface SaveBarProps {
  /** Whether the page holds something the stored settings do not. */
  dirty: boolean;
  saving: boolean;
  canSave: boolean;
  onSave: () => void;
  onDiscard: () => void;
  saveLabel?: string;
  /** Drawn while there is nothing to report about a save. */
  summary: ReactNode;
  outcome: SaveOutcome;
}

export function SaveBar({
  dirty,
  saving,
  canSave,
  onSave,
  onDiscard,
  saveLabel = "Save changes",
  summary,
  outcome,
}: SaveBarProps) {
  if (!dirty && outcome.kind === "none") return null;

  // A failure holds the bar until the next save answers. A live edit outranks a save that landed,
  // so the bar never reports a save while something is unsaved.
  const failure = outcome.kind === "failed" ? outcome.message : null;
  const landed = !dirty && outcome.kind === "saved" ? outcome.message : null;

  return (
    <div className="pointer-events-none fixed inset-x-0 bottom-0 z-50 flex justify-center px-4 py-4">
      {/* py-3.5 is host-absent (Cove's own UI never uses it, so its prebuilt bundle omits it);
          inline the 0.875rem padding instead of shipping CSS. */}
      <div
        className="pointer-events-auto flex w-full max-w-3xl items-center gap-4 rounded-2xl border border-border bg-card px-5 shadow-lg"
        style={{ paddingTop: "0.875rem", paddingBottom: "0.875rem" }}
        aria-busy={saving}
      >
        {/* Cove's own UI writes the red-400 fill only at 80% alpha, so its prebuilt stylesheet omits
            the full-strength utility and the error dot resolved to no fill. The colour scale's custom
            property is declared whether or not a utility using it is, so inline the tone off that. */}
        <span
          className={`h-2 w-2 shrink-0 rounded-full ${
            failure !== null ? "" : landed !== null ? "bg-green-400" : "bg-amber-400"
          }`}
          style={failure !== null ? { backgroundColor: "var(--color-red-400)" } : undefined}
        />
        <div className="min-w-0 flex-1">
          {failure !== null ? (
            <StatusText kind="error">{failure}</StatusText>
          ) : landed !== null ? (
            <StatusText kind="success">{landed}</StatusText>
          ) : (
            summary
          )}
        </div>
        <div className="flex shrink-0 items-center gap-3">
          <Button variant="ghost" onClick={onDiscard} disabled={saving}>
            Discard
          </Button>
          <Button onClick={onSave} disabled={!canSave || saving}>
            {saving ? <Spinner /> : null}
            {saveLabel}
          </Button>
        </div>
      </div>
    </div>
  );
}
