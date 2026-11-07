// @vitest-environment jsdom
import { test, expect, vi, afterEach, beforeEach } from "vitest";
import { createElement } from "react";
import { createRoot, type Root } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";
import {
  answered,
  buttonNamed,
  switchNamed,
  switchStates,
  resetServer as resetOptionsServer,
  server as optionsServer,
} from "./testRunOptions";
import { resetServer as resetRunServer, server as runServer } from "./testRun";

// The cards share one stand-in, split by path, because the page is what holds the options and the
// run together: a test that mounted them apart could not see the Run button refuse a dirty form.
vi.mock("@cove-extensions/ui-shared/extensionRequest", async () => {
  const run = (await import("./testRun")).extensionRequestDouble();
  const options = (await import("./testRunOptions")).extensionRequestDouble();
  const optionsPath = (path: string) => path.endsWith("/options");

  return {
    ...run,
    requestJson: (path: string, init?: RequestInit) =>
      optionsPath(path) ? options.requestJson(path) : run.requestJson(path, init),
    request: (path: string, init?: RequestInit) =>
      optionsPath(path) ? options.request(path, init ?? {}) : run.request(path, init),
  };
});

const { ScanAndIdentifyPage } = await import("./ScanAndIdentifyPage");

let mounted: Root | null = null;

beforeEach(() => {
  resetRunServer();
  resetOptionsServer();
  // No run has ever been started, which is the 204 the route answers.
  runServer.reads = [undefined];
  optionsServer.respond = () => Promise.resolve(answered());
});

afterEach(() => {
  mounted?.unmount();
  mounted = null;
  document.body.replaceChildren();
});

function render(): HTMLElement {
  const container = document.createElement("div");
  document.body.append(container);
  mounted = createRoot(container);
  mounted.render(createElement(ScanAndIdentifyPage));
  return container;
}

// Found without throwing, because the first predicate runs before React has committed anything and
// a throw there ends the wait instead of retrying it.
const runButton = (container: HTMLElement): HTMLButtonElement | undefined =>
  [...container.querySelectorAll("button")].find((b) => b.textContent.trim() === "Run");

async function mountIdle(): Promise<HTMLElement> {
  const container = render();
  await waitFor("the page to offer a run", () => runButton(container)?.disabled === false);
  return container;
}

test("the page mounts the run section and the run options form", async () => {
  const container = await mountIdle();

  expect([...container.querySelectorAll("h3")].map((e) => e.textContent)).toContain("Run options");
});

test("an unsaved option edit refuses a run and says why, without discarding the edit", async () => {
  const container = await mountIdle();

  switchNamed(container, "Sprite sheets").click();
  await waitFor("the run to be refused", () => runButton(container)?.disabled === true);

  expect(container.querySelector("[role='status']")?.textContent).toContain("Save the run options");
  expect(switchStates(container)["Sprite sheets"]).toBe(false);
});

test("saving the edit offers the run again", async () => {
  const container = await mountIdle();
  switchNamed(container, "Sprite sheets").click();
  await waitFor("the run to be refused", () => runButton(container)?.disabled === true);

  buttonNamed(container, "Save").click();
  await waitFor("the run to be offered again", () => runButton(container)?.disabled === false);

  expect(switchStates(container)["Sprite sheets"]).toBe(false);
});
