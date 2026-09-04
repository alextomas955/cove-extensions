/**
 * A control that may be disabled, and that always says why when it is.
 *
 * `Button` carries no `title` and no `aria-*` prop, so the accessible name is composed here instead:
 * the control's own name comes first, then the reason, and a button with no `aria-label` takes its
 * accessible name from its contents in order. The reason is off-screen rather than inline, so forty
 * controls sharing one reason do not each repeat it - a screen states a shared reason once, through
 * `RefusalNotice`.
 *
 * A wrapper rather than an addition to `Button`: the shared primitives file is consumed by every
 * extension, and this system belongs to this one.
 */
import { Button } from "@cove-extensions/ui-shared";

import { OFF_SCREEN } from "./offScreen";

type DisabledControlProps = {
  /** What the control is called. Always announced first, and drawn on screen. */
  name: string;
  onClick: () => void;
  variant?: "primary" | "ghost";
} & (
  | {
      disabled: true;
      /** Required whenever disabled: a dimmed control with nothing to hear is a defect. */
      reason: string;
    }
  | { disabled?: false; reason?: undefined }
);

/**
 * {@link DisabledControl} for a caller that holds the reason and the availability as one value.
 *
 * A reason disables and an absent reason enables, so the pair cannot be set to a disabled control
 * with nothing to hear - the same invariant the prop union above carries, expressed for a caller
 * computing "why not" rather than "whether".
 */
export function OptionallyDisabled({
  name,
  onClick,
  variant,
  reason,
}: {
  name: string;
  onClick: () => void;
  variant?: "primary" | "ghost";
  /** Why the control is unavailable, or null when it is available. */
  reason: string | null;
}) {
  return reason === null ? (
    <DisabledControl name={name} onClick={onClick} variant={variant} />
  ) : (
    <DisabledControl name={name} onClick={onClick} variant={variant} disabled reason={reason} />
  );
}

export function DisabledControl(props: DisabledControlProps) {
  const { name, onClick, variant, disabled } = props;
  const reason = props.disabled === true ? props.reason : undefined;

  return (
    <span title={reason} className="inline-flex">
      <Button variant={variant} onClick={onClick} disabled={disabled}>
        {name}
        {reason === undefined ? null : <span style={OFF_SCREEN}>{reason}</span>}
      </Button>
    </span>
  );
}
