// @vitest-environment jsdom
/**
 * Which chips the legend marks as in use. The tint is the only thing on screen that says a token is
 * already in a template. Which names count as used is `templateUsesToken`'s, and its suite owns
 * those cases.
 *
 * The shared primitives stand in, because their `react` import resolves only inside a consuming
 * bundle.
 */
import { test, expect, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

import { TokenLegend } from "./TokenLegend";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");

  return {
    Chip: function Chip(props: Record<string, unknown>) {
      return h("button", { "data-selected": String(props.selected) }, props.children as ReactNode);
    },
  };
});

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
    .filter((c) => c.getAttribute("data-selected") === "true")
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
