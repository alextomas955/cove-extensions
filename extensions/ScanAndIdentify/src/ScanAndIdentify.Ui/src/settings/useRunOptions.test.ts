// @vitest-environment jsdom
import { test, expect, vi, beforeEach } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";
import type { RunOptions } from "./runOptions";
import { answered, buttonNamed, resetServer, server, switchNamed } from "./testRunOptions";

vi.mock("@cove-extensions/ui-shared/extensionRequest", async () => {
  const { extensionRequestDouble } = await import("./testRunOptions");
  return extensionRequestDouble();
});

const { RunOptionsForm } = await import("./RunOptionsForm");
const { useRunOptions } = await import("./useRunOptions");

// The page owns the controller, so the form is driven here through the same hook the page calls.
function Harness() {
  return createElement(RunOptionsForm, { controller: useRunOptions() });
}

function mount(): HTMLElement {
  const container = document.createElement("div");
  document.body.append(container);
  createRoot(container).render(createElement(Harness));
  return container;
}

const switches = (container: HTMLElement) => container.querySelectorAll('button[role="switch"]');

beforeEach(() => {
  resetServer();
});

test("a read still in flight shows the spinner and no switch", async () => {
  server.respond = () => new Promise<RunOptions>(() => undefined);
  const container = mount();

  await waitFor(
    "the spinner to render",
    () => container.querySelector("svg.animate-spin") !== null,
  );

  expect(switches(container)).toHaveLength(0);
});

test("Save issues one PUT carrying the whole record with the edited value in it", async () => {
  server.respond = () => Promise.resolve(answered());
  const container = mount();
  await waitFor("the switches to render", () => switches(container).length > 0);

  switchNamed(container, "Fill in missing fingerprints").click();
  await waitFor("Save to become enabled", () => !buttonNamed(container, "Save").disabled);
  buttonNamed(container, "Save").click();
  await waitFor("the write to be issued", () => server.writes.length > 0);

  expect(server.writes).toEqual([
    {
      path: "/extensions/com.alextomas955.scanandidentify/options",
      method: "PUT",
      body: JSON.stringify(answered({ backfillFingerprints: false })),
    },
  ]);
});

test("a rejected read shows the failure the request reported and no switch", async () => {
  server.respond = () => Promise.reject(new Error("403 forbidden"));
  const container = mount();

  await waitFor("the failure to render", () => /couldn't read/i.test(container.textContent));

  expect(container.textContent).toContain("403 forbidden");
  expect(switches(container)).toHaveLength(0);
});
