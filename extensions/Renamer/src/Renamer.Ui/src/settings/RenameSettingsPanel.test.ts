// @vitest-environment jsdom
import { test, expect, vi } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

const hook = vi.hoisted(() => ({ load: vi.fn(() => Promise.resolve()) }));

vi.mock("./useRenamerOptions", () => ({
  useRenamerOptions: () => ({
    options: null,
    loading: false,
    loadError: "500 boom",
    load: hook.load,
  }),
}));
vi.mock("./useRenamePreview", () => ({
  useRenamePreview: () => ({ preview: null, previewError: null }),
}));
vi.mock("./useRenameLibrary", () => ({ useRenameLibrary: () => ({}) }));
vi.mock("./useLibraryPaths", () => ({ useLibraryPaths: () => ({ status: "loading" }) }));

const { RenamePanelBody } = await import("./RenameSettingsPanel");

test("a failed first load shows the error and a Retry that loads again, not a spinner", async () => {
  const host = document.createElement("div");
  document.body.append(host);
  createRoot(host).render(createElement(RenamePanelBody));

  await waitFor("the load error", () => host.textContent.includes("500 boom"));
  expect(host.textContent).not.toContain("Loading settings");

  const retry = [...host.querySelectorAll("button")].find((b) => b.textContent === "Retry");
  retry?.click();
  expect(hook.load).toHaveBeenCalledTimes(1);
});
