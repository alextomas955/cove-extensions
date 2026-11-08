// @vitest-environment jsdom
// This test of a shared component lives in the consuming package because `react` resolves only here.
import { test, expect, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup } from "@testing-library/react";

import { ChipMultiSelect } from "@cove-extensions/ui-shared";

afterEach(cleanup);

test("each chip reports to assistive technology whether its value is picked", () => {
  render(
    createElement(ChipMultiSelect, {
      options: [
        { value: "female", label: "Female" },
        { value: "male", label: "Male" },
      ],
      values: ["male"],
      onChange: () => undefined,
    }),
  );

  const pressed = screen
    .getAllByRole("button")
    .map((b) => [b.textContent, b.getAttribute("aria-pressed")]);
  expect(pressed).toEqual([
    ["Female", "false"],
    ["Male", "true"],
  ]);
});
