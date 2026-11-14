// @vitest-environment jsdom
// What the options hook shows from one `GET /options`, and what it sends back.
// Registers the interceptor that turns a failed request into an ApiError carrying its status.
import "../common/lib/wireErrors";

import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import type { RunOptions } from "./runOptions";
import { answered, refusal, resetServer, server } from "./testRunOptions";
import { withQueryClient } from "@cove-extensions/ui-shared/testing";

// The seam is the host's own fetch, which the generated client reaches through the adapter in
// common/lib/wireClient, so what is scripted here is the request the hook really issues.
vi.mock("@cove/runtime/api", async () => {
  const { coveRuntimeApiDouble } = await import("./testRunOptions");
  return coveRuntimeApiDouble();
});

const { RunOptionsForm } = await import("./RunOptionsForm");
const { useRunOptions } = await import("./useRunOptions");

// The page owns the controller, so the form is driven here through the same hook the page calls.
function Harness() {
  return createElement(RunOptionsForm, { controller: useRunOptions() });
}

const mount = () => render(withQueryClient(createElement(Harness)));

const switches = () => screen.queryAllByRole("switch");

beforeEach(resetServer);

afterEach(cleanup);

test("a read still in flight shows the spinner and no switch", async () => {
  server.respond = () => new Promise<RunOptions>(() => undefined);
  const { container } = mount();

  await waitFor(() => {
    expect(container.querySelector("svg.animate-spin")).not.toBeNull();
  });

  expect(switches()).toHaveLength(0);
});

test("Save issues one PUT carrying the whole record with the edited value in it", async () => {
  const user = userEvent.setup();
  server.respond = () => Promise.resolve(answered());
  mount();

  await user.click(await screen.findByRole("switch", { name: "Fill in missing fingerprints" }));
  await user.click(screen.getByRole("button", { name: "Save" }));

  await waitFor(() => {
    expect(server.writes).toEqual([
      {
        path: "/extensions/com.alextomas955.scanandidentify/options",
        method: "PUT",
        body: JSON.stringify(answered({ backfillFingerprints: false })),
      },
    ]);
  });
});

test("a rejected read shows the failure the request reported and no switch", async () => {
  server.readError = refusal(403, '{"detail":"forbidden"}');
  const { container } = mount();

  expect(await screen.findByText(/couldn't read/i)).toBeDefined();
  expect(container.textContent).toContain("403 forbidden");
  expect(switches()).toHaveLength(0);
});
