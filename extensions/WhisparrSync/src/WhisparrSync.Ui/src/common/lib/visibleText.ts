/**
 * The text a reader sees, with the off-screen carriers a control uses for assistive technology
 * left out. Off-screen is read off the inline style the shared control sets, so a carrier that
 * stopped being off-screen shows up here as text.
 */
export function visibleText(element: Element): string {
  return [...element.childNodes]
    .map((node) => {
      if (node.nodeType === Node.TEXT_NODE) {
        return node.textContent ?? "";
      }
      if (!(node instanceof Element)) {
        return "";
      }
      const offScreen = node instanceof HTMLElement && node.style.position === "absolute";
      return offScreen ? "" : visibleText(node);
    })
    .join("");
}
