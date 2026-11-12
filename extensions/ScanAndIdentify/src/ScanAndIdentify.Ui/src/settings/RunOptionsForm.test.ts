// @vitest-environment jsdom
// What the form draws from a loaded record, and what it does with an edit.
// Registers the interceptor that turns a failed request into an ApiError carrying its status.
import "../common/lib/wireErrors";

import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { withQueryClient } from "../common/lib/testQueryClient";
import { answered, refusal, resetServer, server } from "./testRunOptions";
import { RUN_OPTION_FIELDS } from "./runOptions";

// The seam is the host's own fetch, which the generated client reaches through the adapter in
// common/lib/wireClient, so what is scripted here is the request the form really issues.
vi.mock("@cove/runtime/api", async () => {
  const { coveRuntimeApiDouble } = await import("./testRunOptions");
  return coveRuntimeApiDouble();
});

const { RunOptionsForm } = await import("./RunOptionsForm");
const { useRunOptions } = await import("./useRunOptions");

// The page owns the controller so the run card can read its unsaved state, so the form is driven
// here through the same hook the page calls rather than mounted bare.
function Harness() {
  return createElement(RunOptionsForm, { controller: useRunOptions() });
}

const mount = () => render(withQueryClient(createElement(Harness)));

// A record whose values are not all the same, so a switch that renders a constant cannot pass.
const mixed = answered({ rescan: true, thumbnails: false, markOrganized: true });

/** The state every switch is in, keyed by the label that names it. */
const switchStates = (): Record<string, boolean> =>
  Object.fromEntries(
    RUN_OPTION_FIELDS.map((field) => [
      field.label,
      screen.getByRole("switch", { name: field.label }).getAttribute("aria-checked") === "true",
    ]),
  );

const save = () => screen.getByRole<HTMLButtonElement>("button", { name: "Save" });

async function mountLoaded(options = mixed) {
  server.respond = () => Promise.resolve(options);
  const rendered = mount();
  await screen.findByRole("switch", { name: RUN_OPTION_FIELDS[0].label });
  return rendered;
}

beforeEach(resetServer);

afterEach(cleanup);

test("the form offers one switch per field in the record", async () => {
  await mountLoaded();

  expect(RUN_OPTION_FIELDS.map((f) => f.key).toSorted()).toEqual(Object.keys(mixed).toSorted());
  expect(screen.getAllByRole("switch")).toHaveLength(RUN_OPTION_FIELDS.length);
});

test("each switch renders at the value the loaded record carried", async () => {
  await mountLoaded();

  const expected = Object.fromEntries(RUN_OPTION_FIELDS.map((f) => [f.label, mixed[f.key]]));
  expect(switchStates()).toEqual(expected);
  expect(save().disabled).toBe(true);
});

test("toggling one switch enables Save and leaves the other nine alone", async () => {
  const user = userEvent.setup();
  await mountLoaded();
  const before = switchStates();

  await user.click(screen.getByRole("switch", { name: "Sprite sheets" }));
  await waitFor(() => {
    expect(save().disabled).toBe(false);
  });

  const after = switchStates();
  const changed = Object.keys(after).filter((label) => after[label] !== before[label]);
  expect(changed).toEqual(["Sprite sheets"]);
});

test("Discard returns every switch to the loaded value and disables Save", async () => {
  const user = userEvent.setup();
  await mountLoaded();
  const loaded = switchStates();

  await user.click(screen.getByRole("switch", { name: "VR 3D covers and previews" }));
  await user.click(screen.getByRole("switch", { name: "Force rescan (ignore mtime)" }));
  await waitFor(() => {
    expect(save().disabled).toBe(false);
  });

  await user.click(screen.getByRole("button", { name: "Discard" }));
  await waitFor(() => {
    expect(save().disabled).toBe(true);
  });

  expect(switchStates()).toEqual(loaded);
});

test("a save failure reports what the write said and keeps the edited values on screen", async () => {
  const user = userEvent.setup();
  const { container } = await mountLoaded();
  server.writeError = refusal(403, '{"detail":"forbidden"}');

  await user.click(screen.getByRole("switch", { name: "Mark an identified video as organized" }));
  await user.click(save());

  expect(await screen.findByText(/couldn't save/i)).toBeDefined();
  expect(container.textContent).toContain("403 forbidden");
  expect(switchStates()["Mark an identified video as organized"]).toBe(false);
});

test("the form issues no request of its own", async () => {
  const user = userEvent.setup();
  await mountLoaded();

  await user.click(screen.getByRole("switch", { name: "Video previews" }));
  await waitFor(() => {
    expect(save().disabled).toBe(false);
  });

  expect(server.writes).toEqual([]);
});

test("rendering with the stand-in unconfigured reports the refusal rather than throwing", async () => {
  mount();

  expect(await screen.findByText(/couldn't read/i)).toBeDefined();
  expect(screen.queryAllByRole("switch")).toHaveLength(0);
});
