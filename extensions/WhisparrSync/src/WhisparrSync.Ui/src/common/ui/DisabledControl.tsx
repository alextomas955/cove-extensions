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
import type { CSSProperties } from "react";
import { Button } from "@cove-extensions/ui-shared";

/**
 * Off-screen but still in the accessibility tree and still a text node. An inline style rather than
 * a utility class, because the host's Tailwind JIT never scans this bundle and a class it does not
 * emit contributes no declaration at all.
 */
const OFF_SCREEN: CSSProperties = {
  position: "absolute",
  width: "1px",
  height: "1px",
  overflow: "hidden",
  whiteSpace: "nowrap",
  clipPath: "inset(50%)",
};

type DisabledControlProps = {
  /** What the control is called. Announced first, and the only part drawn on screen. */
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
