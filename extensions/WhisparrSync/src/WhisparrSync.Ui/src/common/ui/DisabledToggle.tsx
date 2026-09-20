/**
 * A switch that may be unavailable, and that says why when it is.
 *
 * The reason goes off-screen inside the switch's own label, not only on a hover title, which
 * reaches a pointer alone. A `<button>` is a labelable element, so the label's text in order is the
 * accessible name.
 */
import { Toggle } from "@cove-extensions/ui-shared";

import { OFF_SCREEN } from "./offScreen";

export interface DisabledToggleProps {
  /** Announced first, and drawn on screen. */
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  helper?: string;
  /**
   * Why the switch is unavailable, or null when it is available. A reason disables and an absent
   * reason enables, so an unavailable switch with no reason cannot be expressed.
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
