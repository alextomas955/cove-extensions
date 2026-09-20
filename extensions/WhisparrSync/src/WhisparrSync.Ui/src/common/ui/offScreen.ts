import type { CSSProperties } from "react";

/**
 * Off-screen but still in the accessibility tree and still a text node.
 *
 * An inline style, not a utility class, because the host's Tailwind JIT never scans this bundle
 * and a class it does not emit contributes no declaration.
 */
export const OFF_SCREEN: CSSProperties = {
  position: "absolute",
  width: "1px",
  height: "1px",
  overflow: "hidden",
  whiteSpace: "nowrap",
  clipPath: "inset(50%)",
};
