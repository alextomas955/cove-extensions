// @vitest-environment jsdom
/**
 * Three properties of the upgrade control that only a rendered DOM can settle: that both choices are
 * offered, that the consequence shown is the chosen one's, and that the control cannot be used before
 * the stored value has arrived.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is flushed by waiting rather than by wrapping. The shared
 * primitives stand in, because their `react` import resolves only inside a consuming bundle; each
 * stand-in reproduces the element the real one renders, which is what the assertions read.
 */
import { expect, test, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import type { UpgradeBehavior } from "../wire/api";
import { UPGRADE_DROPS_THE_SUPERSEDED_FILE, UPGRADE_KEEPS_BOTH_FILES } from "../common/ui/copy";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    Field: (props: { label: string; children: ReactNode }) =>
      h("label", null, props.label, props.children),
    Select: (props: {
      value: string;
      disabled?: boolean;
      options: readonly { value: string; label: string }[];
    }) =>
      h(
        "select",
        { value: props.value, disabled: props.disabled, onChange: () => undefined },
        props.options.map((option) =>
          h("option", { key: option.value, value: option.value }, option.label),
        ),
      ),
    SectionCard: (props: { title?: string; description?: string; children: ReactNode }) =>
      h("section", null, props.title, props.description, props.children),
    StatusText: (props: { children: ReactNode }) => h("span", null, props.children),
    Spinner: () => h("span", null, "…"),
  };
});

const { ImportBehaviorSection } = await import("./ImportBehaviorSection");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

async function render(node: ReactNode): Promise<HTMLElement> {
  const host = document.createElement("div");
  document.body.append(host);
  createRoot(host).render(node);
  await sleep(20);
  return host;
}

function section(overrides: {
  behavior?: UpgradeBehavior | null;
  saving?: boolean;
  saveError?: string | null;
  sharedReason?: string | null;
}) {
  return createElement(ImportBehaviorSection, {
    behavior: overrides.behavior === undefined ? "add" : overrides.behavior,
    saving: overrides.saving ?? false,
    saveError: overrides.saveError ?? null,
    sharedReason: overrides.sharedReason ?? null,
    onChange: () => undefined,
  });
}

test("both choices are offered, whichever one is stored", async () => {
  const host = await render(section({ behavior: "add" }));

  const offered = [...host.querySelectorAll("option")].map((option) => option.value);
  expect(offered).toEqual(["add", "replace"]);
});

test("the consequence shown is the chosen one's, and the two do not read the same", async () => {
  const keeping = await render(section({ behavior: "add" }));
  const replacing = await render(section({ behavior: "replace" }));

  expect(keeping.textContent).toContain(UPGRADE_KEEPS_BOTH_FILES);
  expect(keeping.textContent).not.toContain(UPGRADE_DROPS_THE_SUPERSEDED_FILE);
  expect(replacing.textContent).toContain(UPGRADE_DROPS_THE_SUPERSEDED_FILE);
  expect(replacing.textContent).not.toContain(UPGRADE_KEEPS_BOTH_FILES);
});

test("the control cannot be used before the stored value has arrived", async () => {
  const unread = await render(section({ behavior: null }));
  const read = await render(section({ behavior: "add" }));

  expect(unread.querySelector("select")?.disabled).toBe(true);
  // The control: it is enabled once the read has answered, so the disabling above is about the
  // missing value rather than about a control that is never usable.
  expect(read.querySelector("select")?.disabled).toBe(false);
});

test("a save in flight is announced busy and the control is not usable", async () => {
  const host = await render(section({ saving: true }));

  expect(host.querySelector('[aria-busy="true"]')).not.toBeNull();
  expect(host.querySelector("select")?.disabled).toBe(true);
});

test("the shared reason takes the control out without repeating itself beside it", async () => {
  const host = await render(
    section({ sharedReason: "Cove could not read the stored connection." }),
  );

  expect(host.querySelector("select")?.disabled).toBe(true);
  expect(host.textContent).not.toContain("Cove could not read the stored connection.");
});
