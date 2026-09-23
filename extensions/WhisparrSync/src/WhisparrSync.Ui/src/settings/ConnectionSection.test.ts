// @vitest-environment jsdom
import { expect, test } from "vitest";
import { createElement } from "react";

import { render } from "../common/lib/testRender";
import type { WhisparrSyncGenerationSettingsView } from "../wire/api";
import { ConnectionSection } from "./ConnectionSection";
import type { TransientTest } from "./connectLogic";
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
}) {
  return createElement(ConnectionSection, {
    card: "v3",
    stored: overrides.stored === undefined ? NEVER_VERIFIED : overrides.stored,
    readFailed: false,
    draft: overrides.draft ?? NO_DRAFT,
    test: overrides.test ?? { phase: "none" },
    testsStored: false,
    sharedReason: null,
    now: NOW,
    onAddressChange: () => undefined,
    onKeyChange: () => undefined,
    onClearStoredKey: () => undefined,
    onTest: () => undefined,
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
  expect(set.textContent).toContain("Key is set");

  // The response carries no key; SettingsProjectionTests asserts that. So a leak here could only
  // come from the field's own draft.
  const typed = "e2ewriteonly7c41b9a6d2f80e35a1c4";
  const withDraft = await render(section({ draft: { ...NO_DRAFT, apiKey: typed } }));

  expect(withDraft.textContent).not.toContain(typed);
  expect(withDraft.textContent).not.toContain(typed.slice(0, 4));
});
