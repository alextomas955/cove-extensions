// @vitest-environment jsdom
import { test, expect, vi, beforeEach } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";
import type { CapabilitiesView } from "../wire/api";

const server = vi.hoisted(() => ({
  // What the capabilities request answers with. Null until a test sets it, so an unconfigured call
  // fails loudly rather than resolving something nobody chose.
  respond: null as (() => Promise<CapabilitiesView>) | null,
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  errorText: (err: unknown) => (err instanceof Error ? err.message : String(err)),
  requestJson: (path: string) => {
    if (server.respond === null) throw new Error(`unconfigured request to ${path}`);
    return server.respond();
  },
}));

const { CapabilitiesCard } = await import("./CapabilitiesCard");

function mount(): HTMLElement {
  const container = document.createElement("div");
  document.body.append(container);
  createRoot(container).render(createElement(CapabilitiesCard));
  return container;
}

const values = (container: HTMLElement) =>
  [...container.querySelectorAll("dd")].map((e) => e.textContent);

beforeEach(() => {
  server.respond = null;
  document.body.replaceChildren();
});

test("a request still in flight shows the spinner and neither the values nor an error", async () => {
  server.respond = () => new Promise<CapabilitiesView>(() => undefined);
  const container = mount();

  await waitFor(
    "the spinner to render",
    () => container.querySelector("svg.animate-spin") !== null,
  );

  expect(values(container)).toEqual([]);
  expect(container.textContent).not.toMatch(/couldn't read/i);
});

test("a rejected request shows the failure the request reported and no values", async () => {
  server.respond = () => Promise.reject(new Error("403 forbidden"));
  const container = mount();

  await waitFor("the failure to render", () => /couldn't read/i.test(container.textContent));

  expect(container.textContent).toContain("403 forbidden");
  expect(values(container)).toEqual([]);
  expect(container.querySelector("svg.animate-spin")).toBeNull();
});

test.each([
  ["both services resolved", "1.7.0", true, true, ["1.7.0", "available", "available"]],
  ["only the job service resolved", "1.7.0", true, false, ["1.7.0", "available", "unavailable"]],
  ["a host that reported no version", null, true, true, ["not reported", "available", "available"]],
])(
  "a successful read renders %s",
  async (_case, coveVersion, jobService, scanService, expected) => {
    server.respond = () => Promise.resolve({ coveVersion, jobService, scanService });
    const container = mount();

    await waitFor("the reported values to render", () => values(container).length === 3);

    expect(values(container)).toEqual(expected);
    expect(container.querySelector("svg.animate-spin")).toBeNull();
  },
);
