import { act, type ReactNode } from "react";
import { createRoot } from "react-dom/client";
import { afterEach, beforeEach, vi } from "vitest";

const cleanups: (() => void)[] = [];

beforeEach(() => {
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
});

afterEach(async () => {
  try {
    await act(() => {
      while (cleanups.length > 0) cleanups.pop()?.();
      return Promise.resolve();
    });
  } finally {
    vi.unstubAllGlobals();
  }
});

export async function render(node: ReactNode): Promise<HTMLDivElement> {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  cleanups.push(() => {
    root.unmount();
    container.remove();
  });
  await act(() => {
    root.render(node);
    return Promise.resolve();
  });
  return container;
}

export async function press(button: Element | null | undefined): Promise<void> {
  if (button === null || button === undefined) throw new Error("No control found to press");
  await act(() => {
    button.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    return Promise.resolve();
  });
}
