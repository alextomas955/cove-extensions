/**
 * The Dry Run modal's shell: a wide panel with the shared overlay hook's dialog mode (focus trap,
 * Escape and backdrop-click cancel) suspended while an operation is `pending`. A short confirm uses
 * the host's `ConfirmDialog` instead, which is fixed at a narrow width.
 */
import { useCallback, useRef, type ReactNode } from "react";
import { useOverlayKeys } from "@cove-extensions/ui-shared";

export function Dialog({
  titleId,
  describedById,
  pending = false,
  onCancel,
  children,
}: Readonly<{
  /** id of the element that labels the dialog (the title) - wired to aria-labelledby. */
  titleId: string;
  /** optional id of the element that describes the dialog - wired to aria-describedby. */
  describedById?: string;
  /** while true, Esc / scrim-click / programmatic close are suppressed (operation in flight). */
  pending?: boolean;
  onCancel: () => void;
  children: ReactNode;
}>) {
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

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/60" onClick={requestCancel} aria-hidden="true" />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={describedById}
        className="relative mx-4 w-full max-w-5xl rounded-lg border border-border bg-surface p-6 shadow-xl"
      >
        {children}
      </div>
    </div>
  );
}

/** Shared error box (matches Cove `ConfirmDialog`'s destructive error styling). */
export function ErrorBox({ children }: Readonly<{ children: ReactNode }>) {
  return (
    <div className="rounded border border-red-700 bg-red-950/60 px-3 py-2 text-sm text-red-200">
      {children}
    </div>
  );
}
