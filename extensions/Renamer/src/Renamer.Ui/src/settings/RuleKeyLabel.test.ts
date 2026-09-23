// @vitest-environment jsdom
import { test, expect } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";
import { RuleKeyLabel } from "./RuleKeyLabel";

async function render(props: Parameters<typeof RuleKeyLabel>[0]): Promise<string> {
  const host = document.createElement("div");
  const root = createRoot(host);
  root.render(createElement(RuleKeyLabel, props));
  await waitFor("the label to render", () => host.textContent !== "");
  const text = host.textContent;
  root.unmount();
  return text;
}

test("a deleted entity names its kind, the id it held, and that the rule is inert", async () => {
  expect(await render({ entityType: "studio", id: 210, orphaned: true })).toBe(
    "Deleted studio (was #210). This rule no longer applies.",
  );
  expect(await render({ entityType: "tag", id: 7, orphaned: true })).toBe(
    "Deleted tag (was #7). This rule no longer applies.",
  );
});

test("a live entity is left to the host to name", async () => {
  expect(await render({ entityType: "studio", id: 210, orphaned: false })).not.toContain("Deleted");
});
