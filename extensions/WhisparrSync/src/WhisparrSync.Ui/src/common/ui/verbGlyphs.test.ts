// @vitest-environment jsdom
// The table is the one place a verb picks up a shape. Each surface that offers a verb asserts it
// draws the table's mark in its own suite, because a slice may not reach into a sibling slice.
import { expect, test } from "vitest";
import { createElement } from "react";

import { render } from "../lib/testRender";
import { VERB_GLYPH, type WhisparrVerb } from "./verbGlyphs";

const VERBS = Object.keys(VERB_GLYPH) as WhisparrVerb[];

/** The `lucide-<name>` class the drawn svg carries. */
function markIn(element: Element | null): string {
  const classes = element?.querySelector("svg")?.getAttribute("class") ?? "";
  return classes.split(" ").find((one) => one.startsWith("lucide-")) ?? "no mark";
}

test("every verb has a mark to draw", async () => {
  const drawn = await render(
    createElement(
      "div",
      null,
      ...VERBS.map((verb) =>
        createElement(
          "span",
          { key: verb, "data-verb": verb },
          createElement(VERB_GLYPH[verb], {}),
        ),
      ),
    ),
  );

  for (const verb of VERBS) {
    expect(markIn(drawn.querySelector(`[data-verb="${verb}"]`)), verb).not.toBe("no mark");
  }
});

// Two verbs on one surface drawn alike leaves the label as the only thing telling them apart. The
// pairs that do share a mark are the same gesture at a different scope, and never meet on a surface.
test("the verbs a surface offers together are told apart by their marks", async () => {
  const drawn = await render(
    createElement(
      "div",
      null,
      ...VERBS.map((verb) =>
        createElement(
          "span",
          { key: verb, "data-verb": verb },
          createElement(VERB_GLYPH[verb], {}),
        ),
      ),
    ),
  );

  const marks = VERBS.map((verb) => markIn(drawn.querySelector(`[data-verb="${verb}"]`)));
  const shared = marks.filter((mark, index) => marks.indexOf(mark) !== index);

  expect(shared, `two verbs share ${shared.join(", ")}`).toEqual([]);
});
