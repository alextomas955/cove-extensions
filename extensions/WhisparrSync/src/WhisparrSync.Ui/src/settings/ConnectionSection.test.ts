// @vitest-environment jsdom
/**
 * Three properties of the connection form that only a rendered DOM can settle: that a version never
 * verified and a version verified against an instance that has since failed do not read the same,
 * that a pressed control is both announced busy and no longer pressable, and that no part of a key
 * reaches the page.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is flushed by waiting rather than by wrapping. The shared
 * primitives stand in, because their `react` import resolves only inside a consuming bundle; each
 * stand-in reproduces the element the real one renders, which is what the assertions read.
 */
import { expect, test, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import type { WhisparrSyncGenerationSettingsView } from "../wire/api";
import type { GenerationDraft, TransientTest } from "./connectLogic";
import type { SaveState } from "./connectionStore";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    INPUT_CLASS: "input",
    Field: (props: { label: string; helper?: string; children: ReactNode }) =>
      h("label", null, props.label, props.children, props.helper),
    TextInput: (props: { value: string; placeholder?: string; onChange: (v: string) => void }) =>
      h("input", {
        type: "text",
        value: props.value,
        placeholder: props.placeholder,
        onChange: () => undefined,
      }),
    Button: (props: { children?: ReactNode; disabled?: boolean; onClick?: () => void }) =>
      h(
        "button",
        { type: "button", disabled: props.disabled, onClick: props.onClick },
        props.children,
      ),
    SectionCard: (props: { title?: string; description?: string; children: ReactNode }) =>
      h("section", null, props.title, props.description, props.children),
    StatusPill: (props: { children: ReactNode }) => h("span", null, props.children),
    StatusText: (props: { children: ReactNode }) => h("span", null, props.children),
    Spinner: () => h("span", null, "…"),
  };
});

const { ConnectionSection } = await import("./ConnectionSection");

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

const NEVER_VERIFIED: WhisparrSyncGenerationSettingsView = {
  address: "http://whisparr:6969",
  keyIsSet: true,
  recordedVersion: null,
  versionVerifiedAtUtc: null,
  lastReachableAtUtc: null,
};

const VERIFIED: WhisparrSyncGenerationSettingsView = {
  ...NEVER_VERIFIED,
  recordedVersion: "3.3.8.1097",
  versionVerifiedAtUtc: "2026-06-24T11:56:00Z",
  lastReachableAtUtc: "2026-06-24T11:56:00Z",
};

const NOW = Date.parse("2026-06-24T12:00:00Z");

const NO_DRAFT: GenerationDraft = {
  address: "http://whisparr:6969",
  apiKey: "",
  keyCleared: false,
};

function section(overrides: {
  stored?: WhisparrSyncGenerationSettingsView | null;
  draft?: GenerationDraft;
  test?: TransientTest;
  save?: SaveState;
}) {
  return createElement(ConnectionSection, {
    card: "v3",
    stored: overrides.stored === undefined ? NEVER_VERIFIED : overrides.stored,
    readFailed: false,
    draft: overrides.draft ?? NO_DRAFT,
    test: overrides.test ?? { phase: "none" },
    save: overrides.save ?? { status: "idle" },
    noOpSave: false,
    testsStored: false,
    sharedReason: null,
    now: NOW,
    onAddressChange: () => undefined,
    onKeyChange: () => undefined,
    onClearStoredKey: () => undefined,
    onTest: () => undefined,
    onSave: () => undefined,
  });
}

test("a version never verified does not read the same as one verified against an instance that has since failed", async () => {
  const never = await render(section({ stored: NEVER_VERIFIED }));
  const failing = await render(
    section({
      stored: VERIFIED,
      test: { phase: "failed", address: "http://whisparr:6969", message: "500 boom" },
    }),
  );

  expect(never.textContent).toContain("not verified yet");
  // The version and the instant it was read survive a failure that came after them: the reading was
  // true when it was taken, and blanking it would replace a correct answer with none.
  expect(failing.textContent).toContain("3.3.8.1097");
  expect(failing.textContent).not.toContain("not verified yet");
});

test("a pressed control reads as busy and cannot be pressed again", async () => {
  const host = await render(
    section({ test: { phase: "running", address: "http://whisparr:6969" } }),
  );

  const busy = host.querySelector('[aria-busy="true"]');
  expect(
    busy,
    "nothing on the page was announced busy while the test was in flight",
  ).not.toBeNull();

  const pressed = busy?.querySelector("button");
  expect(pressed?.textContent).toContain("Testing");
  expect(pressed?.disabled, "the control could be pressed a second time while in flight").toBe(
    true,
  );
});

test("the key pill reports that a key is set without disclosing any of it", async () => {
  const set = await render(section({ stored: NEVER_VERIFIED }));
  expect(set.textContent).toContain("Key is set");

  // The only key value in scope anywhere on this page: the settings view has no member that could
  // carry a stored one, so a leak could only come from the field's own draft.
  const typed = "e2ewriteonly7c41b9a6d2f80e35a1c4";
  const withDraft = await render(
    section({ draft: { address: "http://whisparr:6969", apiKey: typed, keyCleared: false } }),
  );

  expect(withDraft.textContent).not.toContain(typed);
  expect(withDraft.textContent).not.toContain(typed.slice(0, 4));
});

test("the settings view has no member that could carry a key", () => {
  // Transcribed by hand from the C# record. A member added here would have to be accounted for
  // before the write-only guarantee could still be claimed.
  expect(Object.keys(NEVER_VERIFIED).sort()).toEqual([
    "address",
    "keyIsSet",
    "lastReachableAtUtc",
    "recordedVersion",
    "versionVerifiedAtUtc",
  ]);
});
