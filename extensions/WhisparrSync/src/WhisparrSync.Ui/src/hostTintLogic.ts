/**
 * Partial-alpha theme colors for the status tints, in the same form the host's own stylesheet emits.
 *
 * A Tailwind utility the host's prebuilt stylesheet never emits contributes no declaration and no
 * error, because the host's Tailwind JIT does not scan this bundle: a missing fill leaves the pill
 * transparent and a missing border color falls back to currentColor. Which utilities a host emits
 * depends on what Cove's own source happens to write, so a class can resolve on one supported host
 * and not on the floor `extension.json` declares. Cove defines the color scale as custom properties
 * even for the utilities it never emits, so these follow the host theme rather than freezing a
 * literal. Anything the host does emit stays a class; `check-classes` lists the host-absent ones.
 */
const tint = (variable: string, percent: number) =>
  `color-mix(in oklab, var(${variable}) ${percent}%, transparent)`;

/** The amber "needs attention" tint — border + fill, matching the shared StatusPill's amber variant. */
export const AMBER_TINT = {
  borderColor: tint("--color-amber-400", 40),
  backgroundColor: tint("--color-amber-400", 10),
};

/** The red "problem" tint — fill only; its border stays a host-emitted class. */
export const RED_TINT = { backgroundColor: tint("--color-red-950", 40) };

/** The green "present in library" tint — border + fill. */
export const GREEN_TINT = {
  borderColor: tint("--color-green-500", 40),
  backgroundColor: tint("--color-green-500", 10),
};
