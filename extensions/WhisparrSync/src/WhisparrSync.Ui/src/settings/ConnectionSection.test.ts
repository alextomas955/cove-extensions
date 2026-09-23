// @vitest-environment jsdom
import { expect, test } from "vitest";
import { createElement } from "react";

import { press, render } from "../common/lib/testRender";
import type { WhisparrSyncGenerationSettingsView, WhisparrSyncSettingsView } from "../wire/api";
import { ConnectionSection } from "./ConnectionSection";
import type { CardGeneration, TransientTest } from "./connectLogic";
import type { SettingsDraft } from "./settingsDraftLogic";

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

const NOTHING_STORED: WhisparrSyncGenerationSettingsView = {
  address: "",
  keyIsSet: false,
  recordedVersion: null,
  versionVerifiedAtUtc: null,
  lastReachableAtUtc: null,
};

function settingsWith(v3: WhisparrSyncGenerationSettingsView): WhisparrSyncSettingsView {
  return { selectedGeneration: "v3", upgradeBehavior: "add", v3, v2: NOTHING_STORED };
}

const NOW = Date.parse("2026-06-24T12:00:00Z");

const NO_DRAFT: SettingsDraft = {
  generation: "v3",
  address: "http://whisparr:6969",
  apiKey: "",
  keyCleared: false,
  upgradeBehavior: "add",
};

function section(overrides: {
  stored?: WhisparrSyncGenerationSettingsView | null;
  draft?: SettingsDraft;
  test?: TransientTest;
  sharedReason?: string;
  onChooseGeneration?: (generation: CardGeneration) => void;
}) {
  const stored = overrides.stored === undefined ? NEVER_VERIFIED : overrides.stored;
  return createElement(ConnectionSection, {
    card: "v3",
    settings: stored === null ? null : settingsWith(stored),
    readFailed: false,
    draft: overrides.draft ?? NO_DRAFT,
    test: overrides.test ?? { phase: "none" },
    testsStored: false,
    sharedReason: overrides.sharedReason ?? null,
    now: NOW,
    onAddressChange: () => undefined,
    onKeyChange: () => undefined,
    onClearStoredKey: () => undefined,
    onChooseGeneration: overrides.onChooseGeneration ?? (() => undefined),
    onTest: () => undefined,
  });
}

test("the section says which generation the form is editing, and offers the other one", async () => {
  const chosen: CardGeneration[] = [];
  const host = await render(
    section({ onChooseGeneration: (generation) => chosen.push(generation) }),
  );

  const marked = [...host.querySelectorAll(".grid > *")].filter((option) =>
    option.textContent.includes("Selected"),
  );
  expect(marked, "the section marks no generation as the selected one").toHaveLength(1);
  expect(marked[0].textContent).toContain("Whisparr v3 (Eros)");

  const select = [...host.querySelectorAll("button")].filter((button) =>
    button.textContent.startsWith("Select "),
  );
  expect(select, "the section offers no control that selects the other generation").toHaveLength(1);
  await press(select[0]);
  expect(chosen).toEqual(["v2"]);
});

test("every field label in the section is an uppercase mono micro-label", async () => {
  const host = await render(section({}));

  for (const text of ["Whisparr generation", "Whisparr address", "API key"]) {
    const label = [...host.querySelectorAll("span")].find((span) => span.textContent === text);
    expect(label, `the section draws no label reading ${text}`).toBeDefined();
    expect([...(label?.classList ?? [])], `${text} is not drawn as a mono micro-label`).toEqual(
      expect.arrayContaining(["uppercase", "font-mono", "text-xs", "tracking-wide"]),
    );
  }
});

test("a version never verified does not read the same as one verified against an instance that has since failed", async () => {
  const never = await render(section({ stored: NEVER_VERIFIED }));
  const failing = await render(
    section({
      stored: VERIFIED,
      test: { phase: "failed", address: "http://whisparr:6969", message: "500 boom" },
    }),
  );

  expect(never.textContent).toContain("not verified yet");
  // A later failure does not blank the version or the instant it was read. Both were true when
  // they were taken.
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

  // The generation row states the same thing for each generation, so the pill is taken from
  // outside it: the field's own report is what this asserts.
  const grid = set.querySelector(".grid");
  const pill = [...set.querySelectorAll("span")].filter(
    (span) =>
      span.textContent === "Key is set" &&
      span.childElementCount === 0 &&
      grid?.contains(span) !== true,
  );
  expect(pill, "the key field reported nothing about the stored key").toHaveLength(1);

  // The response carries no key; SettingsProjectionTests asserts that. So a leak here could only
  // come from the field's own draft.
  const typed = "e2ewriteonly7c41b9a6d2f80e35a1c4";
  const withDraft = await render(section({ draft: { ...NO_DRAFT, apiKey: typed } }));

  expect(withDraft.textContent).not.toContain(typed);
  expect(withDraft.textContent).not.toContain(typed.slice(0, 4));
});

test("what the next save will do to the key is stated apart from what is stored", async () => {
  const typed = await render(section({ draft: { ...NO_DRAFT, apiKey: "typed" } }));
  const cleared = await render(section({ draft: { ...NO_DRAFT, keyCleared: true } }));

  // The stored state stays on the field whichever the draft holds; the intent is the extra line.
  expect(typed.textContent).toContain("Key is set");
  expect(typed.textContent).toContain("New key will be saved");

  expect(cleared.textContent).toContain("Key will be removed when you save");
  expect(cleared.textContent).not.toContain("New key will be saved");
});

test("the section offers one accent control, and it is the test", async () => {
  const host = await render(section({}));

  const buttons = [...host.querySelectorAll("button")];
  expect(buttons.length, "the section offers nothing to press").toBeGreaterThan(1);
  const accent = buttons.filter((button) => button.className.split(" ").includes("bg-accent"));
  expect(accent, "the section draws more than one accent control").toHaveLength(1);
  expect(
    accent[0].textContent.startsWith("Test connection"),
    "the section's accent control is something other than the test",
  ).toBe(true);
});

test("a version never verified reads as a normal state, not as a failure", async () => {
  const host = await render(section({ stored: NEVER_VERIFIED }));

  const line = [...host.querySelectorAll("span")].find(
    (span) => span.textContent === "Whisparr version not verified yet",
  );
  expect(line, "the section says nothing about a version it has never verified").toBeDefined();
  expect(line?.className).toContain("text-secondary");
  expect(line?.className).not.toContain("text-red-400");
});

test("a control the page cannot offer names itself before its reason", async () => {
  const reason = "Cove is still reading the store.";
  const host = await render(section({ sharedReason: reason }));

  const refused = [...host.querySelectorAll("button")].filter((button) => button.disabled);
  expect(refused.length, "nothing was refused while the store was unreadable").toBeGreaterThan(0);
  for (const button of refused) {
    expect(
      button.textContent.endsWith(reason),
      "a refusal ends with something other than its reason",
    ).toBe(true);
    expect(button.textContent, "a refused control is its reason and nothing else").not.toBe(reason);
  }
});
