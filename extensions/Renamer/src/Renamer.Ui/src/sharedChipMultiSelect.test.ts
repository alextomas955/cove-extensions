// @vitest-environment jsdom
// This test of a shared component lives in the consuming package because `react` resolves only here.
import { test, expect } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { ChipMultiSelect } from "@cove-extensions/ui-shared";

import { waitFor } from "./common/lib/flushRender";

test("each chip reports to assistive technology whether its value is picked", async () => {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(ChipMultiSelect, {
      options: [
        { value: "female", label: "Female" },
        { value: "male", label: "Male" },
      ],
      values: ["male"],
      onChange: () => undefined,
    }),
  );
  await waitFor("the chips to render", () => container.querySelectorAll("button").length === 2);

  const pressed = [...container.querySelectorAll("button")].map((b) => [
    b.textContent,
    b.getAttribute("aria-pressed"),
  ]);
  expect(pressed).toEqual([
    ["Female", "false"],
    ["Male", "true"],
  ]);

  root.unmount();
  container.remove();
});
