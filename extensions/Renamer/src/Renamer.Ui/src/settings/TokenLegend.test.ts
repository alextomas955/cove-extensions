// @vitest-environment jsdom
// Which chips the legend tints as in use. Which names count as used is `templateUsesToken`'s, and
// its suite owns those cases.
import { test, expect } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

import { TokenLegend } from "./TokenLegend";

async function marked(filenameTemplate: string, folderTemplate: string): Promise<string[]> {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(TokenLegend, { onInsert: () => undefined, filenameTemplate, folderTemplate }),
  );
  await waitFor("the legend chips to render", () => container.querySelector("button") !== null);

  const chips = [...container.querySelectorAll("button")];
  expect(chips.length).toBeGreaterThan(0);
  const result = chips
    .filter((c) => c.classList.contains("bg-accent/15"))
    .map((c) => c.textContent.replace("{ }", "").trim());

  root.unmount();
  container.remove();
  return result;
}

test("a token in either template is marked, and nothing else is", async () => {
  expect(await marked("$title{ [$resolution]}", "$studio/$year")).toEqual([
    "$title",
    "$studio",
    "$year",
    "$resolution",
  ]);
});
