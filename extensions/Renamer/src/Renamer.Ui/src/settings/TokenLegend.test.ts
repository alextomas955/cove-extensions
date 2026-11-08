// @vitest-environment jsdom
// Which chips report their token as already in use, and that the two ways of reporting it agree.
// Which names count as used is `templateUsesToken`'s, and its suite owns those cases.
import { test, expect, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup } from "@testing-library/react";

import { TokenLegend } from "./TokenLegend";

// Copied from the rendered description rather than imported, so the module cannot agree with itself.
const IN_USE = /Already in your template\./;

afterEach(cleanup);

function marked(filenameTemplate: string, folderTemplate: string): string[] {
  render(
    createElement(TokenLegend, { onInsert: () => undefined, filenameTemplate, folderTemplate }),
  );

  const chips = screen.getAllByRole("button");
  // A legend chip inserts its token whether or not a template holds it, so it is a plain button and
  // never reports a pressed state.
  expect(chips.filter((c) => c.hasAttribute("aria-pressed"))).toEqual([]);

  const tinted = chips.filter((c) => c.classList.contains("bg-accent/15"));
  // The tint reaches nobody who cannot see it, so the same chips have to say so in their accessible
  // description. Either set alone would pass while half the users were told nothing.
  expect(
    screen.queryAllByRole("button", { description: IN_USE }),
    "the tint and the description name different chips",
  ).toEqual(tinted);

  return tinted.map((c) => c.textContent.replace("{ }", "").trim());
}

test("a token in either template is marked, and nothing else is", () => {
  expect(marked("$title{ [$resolution]}", "$studio/$year")).toEqual([
    "$title",
    "$studio",
    "$year",
    "$resolution",
  ]);
});

test("a template using no token leaves every chip unmarked", () => {
  expect(marked("fixed name", "")).toEqual([]);
});
