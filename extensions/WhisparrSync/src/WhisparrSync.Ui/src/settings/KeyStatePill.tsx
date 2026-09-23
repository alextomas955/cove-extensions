/**
 * The API key field, with whether a key is stored drawn on the field's own right edge.
 *
 * The key travels in only. No prop can carry a stored key, so the pill reports presence and never
 * a value. What the next save would do to the key is a separate statement the section makes under
 * the field.
 */
import { INPUT_CLASS, StatusPill } from "@cove-extensions/ui-shared";

/**
 * The widest wording this pill draws, measured against the host's own stylesheet, and the offset it
 * is inset by. The host's `pr-*` scale stops short of their sum, so the field's right padding is an
 * inline value and lives here with the pill it has to clear.
 */
export const KEY_PILL_WIDTH_PX = 96;
export const KEY_PILL_OFFSET_PX = 8;
export const KEY_FIELD_PADDING_RIGHT_PX = KEY_PILL_WIDTH_PX + KEY_PILL_OFFSET_PX + 8;

export interface KeyStateFieldProps {
  /** The id the enclosing `Field` owns, so the label names this input. */
  id: string;
  value: string;
  /** Whether a key is stored for this generation, or null while that has not been read. */
  storedKeyIsSet: boolean | null;
  onChange: (next: string) => void;
}

export function KeyStateField({ id, value, storedKeyIsSet, onChange }: KeyStateFieldProps) {
  return (
    <div className="relative">
      <input
        id={id}
        type="password"
        value={value}
        onChange={(e) => {
          onChange(e.target.value);
        }}
        className={INPUT_CLASS}
        style={{ paddingRight: `${KEY_FIELD_PADDING_RIGHT_PX}px` }}
        autoComplete="off"
      />
      {storedKeyIsSet === null ? null : (
        // `-translate-y-1/2` sets `translate`, not `transform`. The pill takes no pointer events, so
        // a press over it reaches the field beneath.
        <span className="pointer-events-none absolute top-1/2 right-2 -translate-y-1/2">
          <StatusPill variant={storedKeyIsSet ? "green" : "gray"}>
            {storedKeyIsSet ? "Key is set" : "Key not stored"}
          </StatusPill>
        </span>
      )}
    </div>
  );
}
