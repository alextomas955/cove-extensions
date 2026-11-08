// @vitest-environment jsdom
// The Required fields explanation sits under its heading and above the control, and the control
// is named by that heading.
import { test, expect, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup } from "@testing-library/react";

import { WhatGetsRenamedSection } from "./WhatGetsRenamedSection";
import { someOptions } from "./testOptions";

const HEADING = "Required fields";
const HELPER = "An item missing any of these is skipped.";

afterEach(cleanup);

function renderSection() {
  render(createElement(WhatGetsRenamedSection, { options: someOptions(), set: () => undefined }));
}

test("the explanation is grouped under its heading, above the control", () => {
  renderSection();

  const helper = screen.getByText(HELPER);
  const control = screen.getByRole("combobox", { name: HEADING });

  const helperPrecedesControl = Boolean(
    helper.compareDocumentPosition(control) & Node.DOCUMENT_POSITION_FOLLOWING,
  );
  expect(helperPrecedesControl).toBe(true);
});

test("the control is named by its heading and nothing else", () => {
  renderSection();

  // The role query matches on the accessible name, so a name assembled any other way - a wrapping
  // label, an `aria-labelledby`, a second visible string - fails here as an `aria-label` read would
  // not. Both queries are made against the constant rather than against each other, so a heading and
  // a label that drifted together would not agree here. Each query throws when it finds nothing.
  screen.getByText(HEADING);
  screen.getByRole("combobox", { name: HEADING });
});
