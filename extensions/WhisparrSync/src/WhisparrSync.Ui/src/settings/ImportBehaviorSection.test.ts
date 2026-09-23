// @vitest-environment jsdom
import { expect, test, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { render as renderNode } from "../common/lib/testRender";

import type { UpgradeBehavior } from "../wire/api";
import { UPGRADE_DROPS_THE_SUPERSEDED_FILE, UPGRADE_KEEPS_BOTH_FILES } from "../common/ui/copy";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    Field: (props: { label: string; children: (controlId: string) => ReactNode }) =>
      h("label", { htmlFor: "control" }, props.label, props.children("control")),
    Select: (props: {
      value: string;
      disabled?: boolean;
      id?: string;
      options: readonly { value: string; label: string }[];
    }) =>
      h(
        "select",
        { id: props.id, value: props.value, disabled: props.disabled, onChange: () => undefined },
        props.options.map((option) =>
          h("option", { key: option.value, value: option.value }, option.label),
        ),
      ),
    SectionCard: (props: { title?: string; description?: string; children: ReactNode }) =>
      h("section", null, props.title, props.description, props.children),
    StatusText: (props: { children: ReactNode }) => h("span", null, props.children),
  };
});

const { ImportBehaviorSection } = await import("./ImportBehaviorSection");

function section(overrides: { behavior?: UpgradeBehavior | null; sharedReason?: string | null }) {
  return createElement(ImportBehaviorSection, {
    behavior: overrides.behavior === undefined ? "add" : overrides.behavior,
    sharedReason: overrides.sharedReason ?? null,
    onChange: () => undefined,
  });
}

test("both choices are offered, whichever one is stored", async () => {
  const host = await renderNode(section({ behavior: "add" }));

  const offered = [...host.querySelectorAll("option")].map((option) => option.value);
  expect(offered).toEqual(["add", "replace"]);
});

test("the consequence shown is the chosen one's, and the two do not read the same", async () => {
  const keeping = await renderNode(section({ behavior: "add" }));
  const replacing = await renderNode(section({ behavior: "replace" }));

  expect(keeping.textContent).toContain(UPGRADE_KEEPS_BOTH_FILES);
  expect(keeping.textContent).not.toContain(UPGRADE_DROPS_THE_SUPERSEDED_FILE);
  expect(replacing.textContent).toContain(UPGRADE_DROPS_THE_SUPERSEDED_FILE);
  expect(replacing.textContent).not.toContain(UPGRADE_KEEPS_BOTH_FILES);
});

test("the control cannot be used before the stored value has arrived", async () => {
  const unread = await renderNode(section({ behavior: null }));
  const read = await renderNode(section({ behavior: "add" }));

  expect(unread.querySelector("select")?.disabled).toBe(true);
  // The control: without this, the check above would pass for a select that is never enabled.
  expect(read.querySelector("select")?.disabled).toBe(false);
});

test("the shared reason takes the control out without repeating itself beside it", async () => {
  const host = await renderNode(
    section({ sharedReason: "Cove could not read the stored connection." }),
  );

  expect(host.querySelector("select")?.disabled).toBe(true);
  expect(host.textContent).not.toContain("Cove could not read the stored connection.");
});
