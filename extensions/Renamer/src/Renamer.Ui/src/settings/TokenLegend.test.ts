// @vitest-environment jsdom
// Which chips the legend tints as in use. Which names count as used is `templateUsesToken`'s, and
// its suite owns those cases.
import { test, expect, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup } from "@testing-library/react";

import { TokenLegend } from "./TokenLegend";

afterEach(cleanup);

function marked(filenameTemplate: string, folderTemplate: string): string[] {
  render(
    createElement(TokenLegend, { onInsert: () => undefined, filenameTemplate, folderTemplate }),
  );

  const chips = screen.getAllByRole("button");
  // A legend chip inserts its token, so it is a plain button whether or not it is tinted.
  expect(chips.filter((c) => c.hasAttribute("aria-pressed"))).toEqual([]);
  return chips
    .filter((c) => c.classList.contains("bg-accent/15"))
    .map((c) => c.textContent.replace("{ }", "").trim());
}

test("a token in either template is marked, and nothing else is", () => {
  expect(marked("$title{ [$resolution]}", "$studio/$year")).toEqual([
    "$title",
    "$studio",
    "$year",
    "$resolution",
  ]);
});
