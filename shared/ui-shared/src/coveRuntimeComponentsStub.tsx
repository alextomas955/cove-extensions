/**
 * What the host draws for `@cove/runtime/components`, for a test that renders a real component
 * reaching it. Cove's import map serves that specifier and nothing else does, so a test run has
 * nothing behind the name until a vitest project aliases it here. The compiler reads the host's real
 * declarations from `.host-types/` instead.
 *
 * The selector renders each chip's Remove button ahead of a search input named only by
 * `inputAriaLabel`, as the host's does, so the settings-panel naming guard sees what a caller passes.
 */

import type * as Host from "@cove/runtime/components";

/** Marks the block the host selector draws, so a caller can address exactly its input. */
export const HOST_SELECTOR_MARK = "data-host-entity-selector";

// Each stand-in takes the host export's own type, so every prop it reads is checked against the
// host's signature.
/** @public */
export const EntityReferenceMultiSelector: typeof Host.EntityReferenceMultiSelector = ({
  values,
  placeholder,
  inputAriaLabel,
}) => (
  <div {...{ [HOST_SELECTOR_MARK]: "" }}>
    {values.map((id) => (
      <span key={id}>
        <button type="button" aria-label={`Remove ${String(id)}`}>
          x
        </button>
      </span>
    ))}
    <input type="text" placeholder={placeholder} aria-label={inputAriaLabel} />
  </div>
);

/** @public */
export const EntityReferenceValue: typeof Host.EntityReferenceValue = ({ value }) => (
  <span>{`entity ${String(value)}`}</span>
);

/** @public */
export const ConfirmDialog: typeof Host.ConfirmDialog = ({
  open,
  title,
  message,
  confirmLabel = "Delete",
  onConfirm,
  onCancel,
}) => {
  if (!open) return null;
  return (
    // Mirrors the host's ConfirmDialog, which is not a native <dialog>.
    <div // NOSONAR
      role="dialog"
      aria-label={title}
    >
      <p>{message}</p>
      <button type="button" onClick={onCancel}>
        Cancel
      </button>
      <button type="button" onClick={() => void onConfirm()}>
        {confirmLabel}
      </button>
    </div>
  );
};
