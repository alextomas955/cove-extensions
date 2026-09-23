/**
 * What the host draws for `@cove/runtime/components`, for a test that renders a real component
 * reaching it. Cove's import map serves that specifier and nothing else does, so a test run has
 * nothing behind the name until a vitest project aliases it here. The run-time counterpart of
 * `coveRuntime.d.ts`, which is the same stand-in for the compiler.
 *
 * The selector renders each chip's Remove button ahead of a search input carrying no name of its
 * own, which is what the host draws on the Cove floor this extension declares. Naming that input
 * here would hide the one gap the settings-panel naming guard records.
 */

/** Marks the block the host selector draws, so a caller can address exactly its input. */
export const HOST_SELECTOR_MARK = "data-host-entity-selector";

export function EntityReferenceMultiSelector({
  values,
  placeholder,
}: Readonly<{ values: number[]; placeholder?: string }>) {
  return (
    <div {...{ [HOST_SELECTOR_MARK]: "" }}>
      {values.map((id) => (
        <span key={id}>
          <button type="button" aria-label={`Remove ${String(id)}`}>
            x
          </button>
        </span>
      ))}
      <input type="text" placeholder={placeholder} />
    </div>
  );
}

export function EntityReferenceValue({ value }: Readonly<{ value: unknown }>) {
  return <span>{`entity ${String(value)}`}</span>;
}

export function ConfirmDialog({
  open,
  title,
  message,
  confirmLabel = "Delete",
  onConfirm,
  onCancel,
}: Readonly<{
  open: boolean;
  title: string;
  message: string;
  confirmLabel?: string;
  onConfirm: () => void | Promise<void>;
  onCancel: () => void;
}>) {
  if (!open) return null;
  return (
    <div role="dialog" aria-label={title}>
      <p>{message}</p>
      <button type="button" onClick={onCancel}>
        Cancel
      </button>
      <button type="button" onClick={() => void onConfirm()}>
        {confirmLabel}
      </button>
    </div>
  );
}
