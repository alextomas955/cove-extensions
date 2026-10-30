// @vitest-environment jsdom
import { test, expect, vi } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

// Both cards read on mount. Their own suites cover what each renders; this one covers that the page
// mounts both, so neither can be dropped from the section the host shows.
vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  errorText: (err: unknown) => (err instanceof Error ? err.message : String(err)),
  requestJson: () => new Promise(() => undefined),
  request: () => Promise.resolve(undefined),
}));

const { ScanAndIdentifyPage } = await import("./ScanAndIdentifyPage");

test("the page mounts the capabilities card and the run options form", async () => {
  const container = document.createElement("div");
  document.body.append(container);
  createRoot(container).render(createElement(ScanAndIdentifyPage));

  await waitFor("both cards to render", () => container.querySelectorAll("section").length === 2);

  const titles = [...container.querySelectorAll("h3")].map((e) => e.textContent);
  expect(titles).toEqual(["Scan and Identify", "Run options"]);
});
