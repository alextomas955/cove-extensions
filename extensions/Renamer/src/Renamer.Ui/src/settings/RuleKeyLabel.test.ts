// @vitest-environment jsdom
import { test, expect, afterEach } from "vitest";
import { createElement } from "react";
import { render, cleanup } from "@testing-library/react";

import { RuleKeyLabel } from "./RuleKeyLabel";

afterEach(cleanup);

function labelText(props: Parameters<typeof RuleKeyLabel>[0]): string {
  const { container, unmount } = render(createElement(RuleKeyLabel, props));
  const text = container.textContent;
  unmount();
  return text;
}

test("a deleted entity names its kind, the id it held, and that the rule is inert", () => {
  expect(labelText({ entityType: "studio", id: 210, orphaned: true })).toBe(
    "Deleted studio (was #210). This rule no longer applies.",
  );
  expect(labelText({ entityType: "tag", id: 7, orphaned: true })).toBe(
    "Deleted tag (was #7). This rule no longer applies.",
  );
});

test("a live entity is left to the host to name", () => {
  // The host component's stand-in renders "entity <id>".
  expect(labelText({ entityType: "studio", id: 210, orphaned: false })).toBe("entity 210");
});
