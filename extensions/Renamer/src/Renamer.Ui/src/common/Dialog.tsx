/**
 * Shared modal shell for the Review and Undo-confirm dialogs.
 *
 * Matches Cove's own `ConfirmDialog` look via semantic tokens (scrim `bg-black/60`, panel
 * `bg-surface rounded-lg border border-border shadow-xl p-6`) so the extension dialog reads as
 * native. Carries `role="dialog"` + `aria-modal` + `aria-labelledby`, a minimal focus trap,
 * Esc-to-cancel and scrim-click-to-cancel — all suppressed while an operation is `pending`.
 *
 * Import audit (see `primitives.tsx`'s header for the full sweep): the barrel-exported
 * `ConfirmDialog` is not a swap for this `Dialog`, for two reasons — its fixed `max-w-sm` gives no size
 * variant, and it is shaped for a single destructive-delete case, carrying its own delete-file and
 * delete-generated state that this shell has no use for.
 *
 * CORRECTED 2026-08-17. This header used to claim the accessibility above was "an intentional a11y
 * improvement over the host baseline" and that `ConfirmDialog` "has none of the above — no
 * `role="dialog"`, no focus trap, no Esc-to-cancel, no scrim-click-cancel". **All four were false**,
 * checked against the host's own `ConfirmDialog`: it sets `role="dialog"` and `aria-modal`, traps Tab
 * across its focusable children with initial focus and focus restore, cancels on Escape unless pending,
 * and cancels on a scrim click unless pending. So this shell matches the host's accessibility rather
 * than exceeding it, and the two size/shape reasons above are the whole of why it still exists. A claim
 * that another component lacks a feature is worth re-reading before it is relied on.
 */
import { useCallback, useRef, type ReactNode } from "react";
import { useOverlayKeys } from "@cove-extensions/ui-shared";

export function Dialog({
  titleId,
  describedById,
  pending = false,
  onCancel,
  size = "lg",
  children,
}: {
  /** id of the element that labels the dialog (the title) — wired to aria-labelledby. */
  titleId: string;
  /** optional id of the element that describes the dialog — wired to aria-describedby. */
  describedById?: string;
  /** while true, Esc / scrim-click / programmatic close are suppressed (operation in flight). */
  pending?: boolean;
  onCancel: () => void;
  size?: "sm" | "lg" | "xl";
  children: ReactNode;
}) {
  const panelRef = useRef<HTMLDivElement>(null);

  const requestCancel = useCallback(() => {
    if (!pending) onCancel();
  }, [pending, onCancel]);

  // closeOnOutsideClick is off because the scrim below keeps its own onClick (an element handler): a
  // document-level outside-click would also fire when host chrome overlays the scrim. enabled tracks
  // !pending so an in-flight operation suspends the cancels.
  useOverlayKeys(panelRef, {
    nav: "dialog",
    onClose: requestCancel,
    enabled: !pending,
    closeOnOutsideClick: false,
  });

  const maxW = size === "sm" ? "max-w-sm" : size === "xl" ? "max-w-5xl" : "max-w-2xl";

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/60" onClick={requestCancel} aria-hidden="true" />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={describedById}
        className={`relative ${maxW} w-full mx-4 rounded-lg border border-border bg-surface p-6 shadow-xl`}
      >
        {children}
      </div>
    </div>
  );
}

/** Shared error box (matches Cove `ConfirmDialog`'s destructive error styling). */
export function ErrorBox({ children }: { children: ReactNode }) {
  return (
    <div className="rounded border border-red-700 bg-red-950/60 px-3 py-2 text-sm text-red-200">
      {children}
    </div>
  );
}
