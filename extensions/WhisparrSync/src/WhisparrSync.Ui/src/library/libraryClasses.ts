/**
 * The class strings the library toolbar control and the card badge strip are built from.
 *
 * Every visual is a host-emitted utility, because an extension stylesheet is page-global and would
 * leak onto every host page. Each string is declared once here and read, so a correction reaches
 * every caller.
 *
 * A host class can be declared in the host stylesheet and still be dead: Cove declares
 * `.border-border` twice and the later `border` shorthand resets the colour. `npm run check-classes`
 * is a deny list and cannot see that, so every class here is owed a computed-style measurement in
 * the browser.
 */

/**
 * The control's own shape, matching Cove's grid, list and wall buttons in the same toolbar group.
 *
 * Icon-only and bare, because the shared `Button` primitive draws a different control from the ones
 * beside it.
 */
export const TOGGLE_CLASS =
  "inline-flex items-center justify-center rounded-lg p-2 transition-colors";

/** The control while the badges are on. This is the only element that carries the accent. */
export const TOGGLE_ON_CLASS = "text-accent";

/** The control while they are off. */
export const TOGGLE_OFF_CLASS = "text-secondary hover:text-foreground";

/** The mark's own size, small enough to sit on one line with the host's own toolbar buttons. */
export const TOGGLE_MARK_CLASS = "h-4 w-4";

/** The mark while the control is off, dimmed so the on state reads as a change in weight too. */
export const TOGGLE_MARK_OFF_CLASS = "opacity-60";

/**
 * The badge's own strip, below the card body.
 *
 * The horizontal and vertical padding are the card body's own, so the badge's left edge lines up
 * with the title in the column above it. The host clips this box, so it is one row.
 */
export const BADGE_STRIP_CLASS =
  "flex items-center gap-1.5 border-t border-border/50 px-2.5 py-1.5";
