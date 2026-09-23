// @vitest-environment jsdom
import { expect, test } from "vitest";
import { createElement } from "react";

import { press, render } from "./testRender";
import { SaveBar, type SaveOutcome } from "./saveBar";

const SUMMARY = "The Whisparr address is not saved yet.";

function bar(overrides: {
  dirty?: boolean;
  saving?: boolean;
  canSave?: boolean;
  outcome?: SaveOutcome;
  onSave?: () => void;
  onDiscard?: () => void;
}) {
  return createElement(SaveBar, {
    dirty: overrides.dirty ?? true,
    saving: overrides.saving ?? false,
    canSave: overrides.canSave ?? true,
    onSave: overrides.onSave ?? (() => undefined),
    onDiscard: overrides.onDiscard ?? (() => undefined),
    summary: SUMMARY,
    outcome: overrides.outcome ?? { kind: "none" },
  });
}

test("nothing is drawn while nothing is unsaved and no save is being reported", async () => {
  const clean = await render(bar({ dirty: false }));
  const unsaved = await render(bar({ dirty: true }));

  expect(clean.textContent).toBe("");
  expect(unsaved.textContent).not.toBe("");
});

test("what is unsaved is named, and both controls are offered", async () => {
  const host = await render(bar({}));

  expect(host.textContent).toContain(SUMMARY);
  const names = [...host.querySelectorAll("button")].map((button) => button.textContent);
  expect(names).toEqual(["Discard", "Save changes"]);
});

test("the save control takes the name the page gives it", async () => {
  const host = await render(
    createElement(SaveBar, {
      dirty: true,
      saving: false,
      canSave: true,
      onSave: () => undefined,
      onDiscard: () => undefined,
      summary: SUMMARY,
      outcome: { kind: "none" },
      saveLabel: "Save settings",
    }),
  );

  expect(host.textContent).toContain("Save settings");
});

test("a save in flight announces itself busy and neither control can be pressed", async () => {
  let saves = 0;
  const host = await render(
    bar({
      saving: true,
      onSave: () => {
        saves += 1;
      },
    }),
  );

  expect(host.querySelector('[aria-busy="true"]')).not.toBeNull();
  const buttons = [...host.querySelectorAll("button")];
  expect(buttons.map((button) => button.disabled)).toEqual([true, true]);

  await press(buttons[1]);
  expect(saves).toBe(0);
});

test("a save that cannot be made leaves the discard control usable", async () => {
  const host = await render(bar({ canSave: false }));

  const buttons = [...host.querySelectorAll("button")];
  expect(buttons.map((button) => button.disabled)).toEqual([false, true]);
});

test("each control reports the press it took", async () => {
  let saves = 0;
  let discards = 0;
  const host = await render(
    bar({
      onSave: () => {
        saves += 1;
      },
      onDiscard: () => {
        discards += 1;
      },
    }),
  );

  const buttons = [...host.querySelectorAll("button")];
  await press(buttons[0]);
  await press(buttons[1]);

  expect([discards, saves]).toEqual([1, 1]);
});

test("a failed save states the reason", async () => {
  const host = await render(
    bar({ outcome: { kind: "failed", message: "Cove could not save: 500 boom" } }),
  );

  expect(host.textContent).toContain("500 boom");
  expect(host.textContent).not.toContain(SUMMARY);
});

// A failed save leaves the draft in the form, so the bar is dirty and still has to say why.
test("a failed save outranks what is still unsaved", async () => {
  const host = await render(
    bar({ dirty: true, outcome: { kind: "failed", message: "Cove could not save: 500 boom" } }),
  );

  expect(host.textContent).toContain("500 boom");
});

test("a save that landed is reported once nothing is left unsaved", async () => {
  const host = await render(bar({ dirty: false, outcome: { kind: "saved", message: "Saved." } }));

  expect(host.textContent).toContain("Saved.");
});

test("an edit after a save that landed replaces that report with the unsaved summary", async () => {
  const host = await render(bar({ dirty: true, outcome: { kind: "saved", message: "Saved." } }));

  expect(host.textContent).toContain(SUMMARY);
  expect(host.textContent).not.toContain("Saved.");
});

// Each state has its own sentence, so the dot repeats what is already written beside it.
test("the three states read differently with no colour read", async () => {
  const unsaved = await render(bar({ dirty: true }));
  const saved = await render(bar({ dirty: false, outcome: { kind: "saved", message: "Saved." } }));
  const failed = await render(
    bar({ outcome: { kind: "failed", message: "Cove could not save: 500 boom" } }),
  );

  const sentences = [unsaved, saved, failed].map((host) => host.textContent);
  expect(new Set(sentences).size).toBe(3);
  for (const sentence of sentences) expect(sentence).not.toBe("");
});
