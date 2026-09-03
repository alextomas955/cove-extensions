import type { CSSProperties } from "react";

/**
 * Off-screen but still in the accessibility tree and still a text node.
 *
 * An inline style rather than a utility class, because the host's Tailwind JIT never scans this
 * bundle and a class it does not emit contributes no declaration at all.
 *
 * Its own module so a control can carry a reason without pulling a component - and everything that
 * component imports - along with it.
 */
export const OFF_SCREEN: CSSProperties = {
  position: "absolute",
  width: "1px",
  height: "1px",
  overflow: "hidden",
  whiteSpace: "nowrap",
  clipPath: "inset(50%)",
};
