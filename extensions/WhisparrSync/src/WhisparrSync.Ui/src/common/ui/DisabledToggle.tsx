/**
 * A switch that may be unavailable, and that always says why when it is.
 *
 * The reason goes off-screen inside the switch's own label element rather than only on a hover
 * title: a `<button>` is a labelable element, so the label's text in order is the switch's
 * accessible name, and appending the reason after the visible name announces name then reason. A
 * title alone reaches a pointer only.
 *
 * A wrapper rather than an addition to `Toggle`: the shared primitives file is consumed by every
 * extension, and this system belongs to this one.
 */
import { Toggle } from "@cove-extensions/ui-shared";

import { OFF_SCREEN } from "./offScreen";

export interface DisabledToggleProps {
  /** What the switch is called. Always announced first, and drawn on screen. */
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  helper?: string;
  /**
   * Why the switch is unavailable, or null when it is available. A reason disables and an absent
   * reason enables, so an unavailable switch with nothing to hear cannot be expressed.
   */
  reason: string | null;
}

export function DisabledToggle({ label, checked, onChange, helper, reason }: DisabledToggleProps) {
  if (reason === null) {
    return <Toggle label={label} checked={checked} onChange={onChange} helper={helper} />;
  }

  return (
    <div title={reason}>
      <Toggle
        label={
          <>
            {label}
            <span style={OFF_SCREEN}>{reason}</span>
          </>
        }
        checked={checked}
        onChange={onChange}
        helper={helper}
        disabled
      />
    </div>
  );
}
