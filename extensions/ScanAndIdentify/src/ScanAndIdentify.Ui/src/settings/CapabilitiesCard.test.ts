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

const rendered = (container: HTMLElement) => container.querySelector("section") !== null;

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

  expect(container.textContent).not.toMatch(/couldn't read/i);
});

test("a rejected request shows the failure the request reported and no values", async () => {
  server.respond = () => Promise.reject(new Error("403 forbidden"));
  const container = mount();

  await waitFor("the failure to render", () => /couldn't read/i.test(container.textContent));

  expect(container.textContent).toContain("403 forbidden");
  expect(container.querySelector("svg.animate-spin")).toBeNull();
});

test("a host that supplied everything renders nothing at all", async () => {
  server.respond = () =>
    Promise.resolve({ coveVersion: "1.7.0", jobService: true, scanService: true });
  const container = mount();

  await waitFor("the spinner to go", () => container.querySelector("svg.animate-spin") === null);

  expect(rendered(container)).toBe(false);
  expect(container.textContent).toBe("");
});

test.each([
  ["the scan service", true, false, "the scan service"],
  ["the job service", false, true, "the job service"],
  ["both services", false, false, "the job service or the scan service"],
])(
  "a host missing %s says so and names the version",
  async (_case, jobService, scanService, named) => {
    server.respond = () => Promise.resolve({ coveVersion: "1.7.0", jobService, scanService });
    const container = mount();

    await waitFor("the warning to render", () => rendered(container));

    expect(container.textContent).toContain(named);
    expect(container.textContent).toContain("a run cannot start");
    expect(container.textContent).toContain("1.7.0");
  },
);
