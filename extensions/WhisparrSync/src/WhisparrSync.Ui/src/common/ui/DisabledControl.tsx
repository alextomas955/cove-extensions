/**
 * A control that may be disabled, and that says why when it is.
 *
 * The host's `Button` carries no `title` and no `aria-*` prop, so the accessible name is composed
 * from its contents: the name first, then the reason. The reason is off-screen, so many controls
 * sharing one reason do not each draw it. A screen states a shared reason once, through
 * `RefusalNotice`.
 */
import { Button } from "@cove-extensions/ui-shared";

import { OFF_SCREEN } from "./offScreen";

type DisabledControlProps = {
  /** Announced first, and drawn on screen. */
  name: string;
  onClick: () => void;
  variant?: "primary" | "ghost";
  /** Draw the control as a full-width bar. */
  fill?: boolean;
} & (
  | {
      disabled: true;
      /** Required whenever disabled: a dimmed control with nothing to hear is a defect. */
      reason: string;
    }
  | { disabled?: false; reason?: undefined }
);

/**
 * {@link DisabledControl} for a caller that holds the reason and the availability as one value. A
 * reason disables and an absent reason enables, so a disabled control with no reason cannot be
 * expressed.
 */
export function OptionallyDisabled({
  name,
  onClick,
  variant,
  fill,
  reason,
}: Readonly<{
  name: string;
  onClick: () => void;
  variant?: "primary" | "ghost";
  /** Draw the control as a full-width bar. */
  fill?: boolean;
  /** Why the control is unavailable, or null when it is available. */
  reason: string | null;
}>) {
  return reason === null ? (
    <DisabledControl name={name} onClick={onClick} variant={variant} fill={fill} />
  ) : (
    <DisabledControl
      name={name}
      onClick={onClick}
      variant={variant}
      fill={fill}
      disabled
      reason={reason}
    />
  );
}

export function DisabledControl(props: Readonly<DisabledControlProps>) {
  const { name, onClick, variant, fill, disabled } = props;
  const reason = props.disabled === true ? props.reason : undefined;

  return (
    <span title={reason} className={fill === true ? "flex w-full" : "inline-flex"}>
      <Button variant={variant} onClick={onClick} disabled={disabled} fill={fill}>
        {name}
        {reason === undefined ? null : <span style={OFF_SCREEN}>{reason}</span>}
      </Button>
    </span>
  );
}
