// @vitest-environment jsdom
import { createElement } from "react";
import { expect, test, vi } from "vitest";

import { press, render } from "../common/lib/testRender";
import type { MissingEntryMode } from "../wire/api";
import { missingEntryChoice } from "./missingEntryLogic";

vi.mock("./hostComponents", () => ({
  ConfirmDialog: ({
    title,
    message,
    confirmLabel,
    onConfirm,
    onCancel,
  }: {
    title: string;
    message: string;
    confirmLabel: string;
    onConfirm: () => void;
    onCancel: () => void;
  }) =>
    createElement("div", { role: "dialog", "aria-label": title }, [
      createElement("p", { key: "message" }, message),
      createElement("button", { key: "confirm", type: "button", onClick: onConfirm }, confirmLabel),
      createElement("button", { key: "cancel", type: "button", onClick: onCancel }, "Cancel"),
    ]),
}));

const { MissingEntryDialog } = await import("./MissingEntryDialog");

const EVERY_MODE: MissingEntryMode[] = ["trackOnly", "monitorNewReleases", "monitorAllScenes"];

async function open(chosen: (mode: MissingEntryMode | null) => void, oneWayDoor = true) {
  await render(
    createElement(MissingEntryDialog, {
      choice: missingEntryChoice({
        modes: EVERY_MODE,
        scopeChangeIsRetroactive: oneWayDoor ? false : true,
      }),
      onChoose: chosen,
    }),
  );
}

function rows(): HTMLButtonElement[] {
  return [...document.querySelectorAll('[role="menuitem"]')] as HTMLButtonElement[];
}

test("offers one row per mode the instance named, and a way out", async () => {
  await open(() => undefined);

  expect(rows().map((row) => row.textContent)).toEqual([
    "Track onlyWhisparr will then list every scene it knows of. " +
      "Nothing is monitored or downloaded until you say so.",
    "New releases only",
    "All scenes",
    "Cancel",
  ]);
});

test("answers a row that marks nothing straight away", async () => {
  const answers: (MissingEntryMode | null)[] = [];
  await open((mode) => answers.push(mode));

  await press(rows()[1]);

  expect(answers).toEqual(["monitorNewReleases"]);
});

test("answers the back-catalogue row only once the confirmation is accepted", async () => {
  const answers: (MissingEntryMode | null)[] = [];
  await open((mode) => answers.push(mode));

  await press(rows()[2]);

  expect(answers, "nothing is answered while the confirmation stands").toEqual([]);
  expect(document.querySelector('[role="dialog"]')?.textContent).toContain(
    "Narrowing the scope back to new releases only does not undo this",
  );

  await press(document.querySelectorAll('[role="dialog"] button')[0]);

  expect(answers).toEqual(["monitorAllScenes"]);
});

test("leaves the one-way door out of the confirmation where a scope change undoes it", async () => {
  await open(() => undefined, false);

  await press(rows()[2]);

  const message = document.querySelector('[role="dialog"]')?.textContent ?? "";

  expect(message).toContain("This monitors every scene Whisparr already lists");
  expect(message).not.toContain("does not undo this");
});
