// @vitest-environment jsdom
// The two cards together: the page is what holds the options and the run, so only here can a test
// see the Run button refuse a dirty form.
// Registers the interceptor that turns a failed request into an ApiError carrying its status.
import "../common/lib/wireErrors";

import { test, expect, vi, afterEach, beforeEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import {
  answered,
  resetServer as resetOptionsServer,
  server as optionsServer,
} from "./testRunOptions";
import { resetServer as resetRunServer, server as runServer } from "./testRun";
import { withQueryClient } from "@cove-extensions/ui-shared/testing";

// One stand-in split by route, because both cards reach the host through the same fetch.
vi.mock("@cove/runtime/api", async () => {
  const run = (await import("./testRun")).coveRuntimeApiDouble();
  const options = (await import("./testRunOptions")).coveRuntimeApiDouble();

  return {
    extensionFetch: (path: string, init?: RequestInit) =>
      path.endsWith("/options")
        ? options.extensionFetch(path, init)
        : run.extensionFetch(path, init),
  };
});

const { ScanAndIdentifyPage } = await import("./ScanAndIdentifyPage");

const runButton = () => screen.getByRole<HTMLButtonElement>("button", { name: "Run" });

const spriteSwitch = () => screen.getByRole("switch", { name: "Sprite sheets" });

beforeEach(() => {
  resetRunServer();
  resetOptionsServer();
  // No run has ever been started, which is the 204 the route answers.
  runServer.reads = [undefined];
  optionsServer.respond = () => Promise.resolve(answered());
});

afterEach(cleanup);

async function mountIdle() {
  const rendered = render(withQueryClient(createElement(ScanAndIdentifyPage)));
  // Both cards: the options form is what a later edit acts on, and the run button is what it has
  // to refuse. Waiting on one alone leaves the other still loading.
  await screen.findByRole("switch", { name: "Sprite sheets" });
  await waitFor(() => {
    expect(runButton().disabled).toBe(false);
  });
  return rendered;
}

test("the page mounts the run section and the run options form", async () => {
  await mountIdle();

  expect(screen.getByRole("heading", { name: "Run options" })).toBeDefined();
});

test("an unsaved option edit refuses a run and says why, without discarding the edit", async () => {
  const user = userEvent.setup();
  await mountIdle();

  await user.click(spriteSwitch());
  await waitFor(() => {
    expect(runButton().disabled).toBe(true);
  });

  expect(screen.getByRole("status").textContent).toContain("Save the run options");
  expect(spriteSwitch().getAttribute("aria-checked")).toBe("false");
});

test("saving the edit offers the run again", async () => {
  const user = userEvent.setup();
  await mountIdle();

  await user.click(spriteSwitch());
  await waitFor(() => {
    expect(runButton().disabled).toBe(true);
  });

  await user.click(screen.getByRole("button", { name: "Save" }));
  await waitFor(() => {
    expect(runButton().disabled).toBe(false);
  });

  expect(spriteSwitch().getAttribute("aria-checked")).toBe("false");
});
