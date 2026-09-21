/**
 * The class strings the library toolbar control and the card badge strip are built from.
 *
 * Every visual is a host-emitted utility, because an extension stylesheet is page-global and would
 * leak onto every host page.
 *
 * A host class can be declared in the host stylesheet and still be dead: Cove declares
 * `.border-border` twice and the later `border` shorthand resets the colour. `npm run
 * check-classes` is a deny list and cannot see that, so every class here needs a computed-style
 * measurement in the browser.
 */

// Icon-only and bare, matching Cove's grid, list and wall buttons in the same toolbar group. The
// shared `Button` primitive draws a different control from the ones beside it.
export const TOGGLE_CLASS =
  "inline-flex items-center justify-center rounded-lg p-2 transition-colors";

export const TOGGLE_ON_CLASS = "text-accent";

export const TOGGLE_OFF_CLASS = "text-secondary hover:text-foreground";

// Small enough to sit on one line with the host's own toolbar buttons.
export const TOGGLE_MARK_CLASS = "h-4 w-4";

export const TOGGLE_MARK_OFF_CLASS = "opacity-60";

// The padding is the card body's own, so the badge's left edge lines up with the title above it.
// The host clips this box, so it is one row.
export const BADGE_STRIP_CLASS =
  "flex items-center gap-1.5 border-t border-border/50 px-2.5 py-1.5";
