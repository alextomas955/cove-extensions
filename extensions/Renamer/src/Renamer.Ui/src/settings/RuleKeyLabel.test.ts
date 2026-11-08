// @vitest-environment jsdom
import { test, expect, vi, afterEach } from "vitest";
import { createElement } from "react";
import { render, cleanup } from "@testing-library/react";

import { RuleKeyLabel } from "./RuleKeyLabel";

// What the live branch hands the host, recorded instead of rendered. The wording a live entity reads
// as is the host's own, so a test that asserted it would go red on a reworded stand-in.
const hostNamed = vi.hoisted(() => [] as { entityType: string; value: number }[]);

vi.mock("@cove/runtime/components", () => ({
  EntityReferenceValue: ({ entityType, value }: { entityType: string; value: number }) => {
    hostNamed.push({ entityType, value });
    return null;
  },
}));

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
  expect(hostNamed, "a deleted entity was handed to the host to name").toEqual([]);
});

test("a live entity is left to the host to name", () => {
  const text = labelText({ entityType: "studio", id: 210, orphaned: false });

  expect(hostNamed).toEqual([{ entityType: "studio", value: 210 }]);
  expect(text).not.toContain("Deleted");
});
