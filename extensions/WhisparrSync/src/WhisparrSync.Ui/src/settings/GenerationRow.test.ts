// @vitest-environment jsdom
import { expect, test } from "vitest";
import { createElement } from "react";

import { press, render } from "../common/lib/testRender";
import type { WhisparrSyncSettingsView } from "../wire/api";
import { GenerationRow } from "./GenerationRow";
import type { CardGeneration } from "./connectLogic";

const BOTH_STORED: WhisparrSyncSettingsView = {
  selectedGeneration: "v3",
  upgradeBehavior: "add",
  v3: {
    address: "http://whisparr3:6969",
    keyIsSet: true,
    recordedVersion: null,
    versionVerifiedAtUtc: null,
    lastReachableAtUtc: null,
  },
  v2: {
    address: "",
    keyIsSet: false,
    recordedVersion: null,
    versionVerifiedAtUtc: null,
    lastReachableAtUtc: null,
  },
};

function row(overrides: {
  settings?: WhisparrSyncSettingsView | null;
  drafted?: CardGeneration;
  sharedReason?: string | null;
  onChoose?: (generation: CardGeneration) => void;
}) {
  return createElement(GenerationRow, {
    settings: overrides.settings === undefined ? BOTH_STORED : overrides.settings,
    drafted: overrides.drafted ?? "v3",
    sharedReason: overrides.sharedReason ?? null,
    onChoose: overrides.onChoose ?? (() => undefined),
  });
}

function controlNames(host: HTMLElement): (string | null)[] {
  return [...host.querySelectorAll("button")].map((button) => button.textContent);
}

/** The two option boxes, in the order the row draws them. */
function options(host: HTMLElement): Element[] {
  const grid = host.querySelector(".grid");
  expect(grid, "the row drew no option grid").not.toBeNull();
  return [...(grid?.children ?? [])];
}

test("both generations are drawn whichever one the draft holds", async () => {
  const onV3 = await render(row({ drafted: "v3" }));
  const onV2 = await render(row({ drafted: "v2" }));

  for (const host of [onV3, onV2]) {
    expect(host.textContent).toContain("Whisparr v3 (Eros)");
    expect(host.textContent).toContain("Whisparr v2");
  }
});

test("the drafted option is marked by a word, not by its colours alone", async () => {
  const host = await render(row({ drafted: "v2" }));

  const marked = options(host).filter((option) => option.textContent.includes("Selected"));
  expect(marked, "the drafted option carries no word saying it is the selected one").toHaveLength(
    1,
  );

  // A mark beside the word, as every other status pill the product draws carries one.
  expect(marked[0]?.querySelector("svg"), "the pill says it with a tint and a word alone").not.toBe(
    null,
  );
  expect(marked[0].textContent).toContain("Whisparr v2");
  expect(marked[0].textContent).not.toContain("Eros");
});

test("only the option the draft does not hold carries a control, and it names that generation", async () => {
  const host = await render(row({ drafted: "v3" }));

  expect(controlNames(host)).toEqual(["Select Whisparr v2"]);
});

test("pressing the control reports the generation it names", async () => {
  const chosen: CardGeneration[] = [];
  const host = await render(
    row({
      drafted: "v2",
      onChoose: (generation) => chosen.push(generation),
    }),
  );

  await press(host.querySelector("button"));

  expect(chosen).toEqual(["v3"]);
});

test("each option names its own stored address and its own key state", async () => {
  const [v3, v2] = options(await render(row({ drafted: "v3" })));

  expect(v3.textContent).toContain("http://whisparr3:6969");
  expect(v3.textContent).toContain("Key is set");
  // The v2 half is stored and empty, which reads differently from stored and set.
  expect(v2.textContent).toContain("No address stored");
  expect(v2.textContent).toContain("Key not stored");
});

test("an option never draws any part of a key", async () => {
  const host = await render(row({ drafted: "v3" }));

  expect(host.textContent).not.toContain("keyIsSet");
  expect(host.querySelectorAll('input[type="password"]')).toHaveLength(0);
});

test("stored values that have not arrived do not read as nothing stored", async () => {
  const host = await render(row({ settings: null, sharedReason: "Cove is still reading." }));

  expect(host.textContent).not.toContain("No address stored");
  expect(host.textContent).not.toContain("Key not stored");
  expect(host.textContent).toContain("Not read yet");
});

test("before the read answers the control cannot be pressed and names itself before its reason", async () => {
  const reason = "Cove is still reading the stored connections.";
  const host = await render(row({ settings: null, sharedReason: reason }));

  const control = host.querySelector("button");
  expect(control?.disabled, "the control could be pressed before anything was read").toBe(true);
  expect(control?.textContent).toBe(`Select Whisparr v2${reason}`);
});
