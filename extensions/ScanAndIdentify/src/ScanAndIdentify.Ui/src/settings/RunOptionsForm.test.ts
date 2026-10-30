// @vitest-environment jsdom
import { test, expect, vi, beforeEach } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";
import {
  answered,
  buttonNamed,
  resetServer,
  server,
  switchNamed,
  switchStates,
} from "./testRunOptions";
import { RUN_OPTION_FIELDS } from "./runOptions";

vi.mock("@cove-extensions/ui-shared/extensionRequest", async () => {
  const { extensionRequestDouble } = await import("./testRunOptions");
  return extensionRequestDouble();
});

const { RunOptionsForm } = await import("./RunOptionsForm");

function mount(): HTMLElement {
  const container = document.createElement("div");
  document.body.append(container);
  createRoot(container).render(createElement(RunOptionsForm));
  return container;
}

const switches = (container: HTMLElement) => container.querySelectorAll('button[role="switch"]');

// A record whose values are not all the same, so a switch that renders a constant cannot pass.
const mixed = answered({ rescan: true, thumbnails: false, markOrganized: true });

async function mountLoaded(options = mixed): Promise<HTMLElement> {
  server.respond = () => Promise.resolve(options);
  const container = mount();
  await waitFor("every switch to render", () => switches(container).length === 10);
  return container;
}

beforeEach(() => {
  resetServer();
});

test("the form offers one switch per field in the record", async () => {
  const container = await mountLoaded();

  expect(RUN_OPTION_FIELDS.map((f) => f.key).toSorted()).toEqual(Object.keys(mixed).toSorted());
  expect(Object.keys(switchStates(container)).toSorted()).toEqual(
    RUN_OPTION_FIELDS.map((f) => f.label).toSorted(),
  );
});

test("each switch renders at the value the loaded record carried", async () => {
  const container = await mountLoaded();

  const expected = Object.fromEntries(RUN_OPTION_FIELDS.map((f) => [f.label, mixed[f.key]]));
  expect(switchStates(container)).toEqual(expected);
  expect(buttonNamed(container, "Save").disabled).toBe(true);
});

test("toggling one switch enables Save and leaves the other nine alone", async () => {
  const container = await mountLoaded();
  const before = switchStates(container);

  switchNamed(container, "Sprite sheets").click();
  await waitFor("Save to become enabled", () => !buttonNamed(container, "Save").disabled);

  const after = switchStates(container);
  const changed = Object.keys(after).filter((label) => after[label] !== before[label]);
  expect(changed).toEqual(["Sprite sheets"]);
});

test("Discard returns every switch to the loaded value and disables Save", async () => {
  const container = await mountLoaded();
  const loaded = switchStates(container);

  switchNamed(container, "VR 3D covers and previews").click();
  switchNamed(container, "Force rescan (ignore mtime)").click();
  await waitFor("Save to become enabled", () => !buttonNamed(container, "Save").disabled);

  buttonNamed(container, "Discard").click();
  await waitFor("Save to become disabled again", () => buttonNamed(container, "Save").disabled);

  expect(switchStates(container)).toEqual(loaded);
});

test("a save failure reports what the write said and keeps the edited values on screen", async () => {
  const container = await mountLoaded();
  server.writeError = new Error("403 forbidden");

  switchNamed(container, "Mark an identified video as organized").click();
  await waitFor("Save to become enabled", () => !buttonNamed(container, "Save").disabled);
  buttonNamed(container, "Save").click();
  await waitFor("the failure to render", () => /couldn't save/i.test(container.textContent));

  expect(container.textContent).toContain("403 forbidden");
  expect(switchStates(container)["Mark an identified video as organized"]).toBe(false);
});

test("the form issues no request of its own", async () => {
  const container = await mountLoaded();
  switchNamed(container, "Video previews").click();
  await waitFor("Save to become enabled", () => !buttonNamed(container, "Save").disabled);

  expect(server.writes).toEqual([]);
});

test("rendering with the stand-in unconfigured reports the refusal rather than throwing", async () => {
  const container = mount();

  await waitFor("the failure to render", () => /couldn't read/i.test(container.textContent));

  expect(switches(container)).toHaveLength(0);
});
