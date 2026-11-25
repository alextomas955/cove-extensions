// @vitest-environment jsdom
// Registers the interceptor that turns a failed request into an ApiError carrying its status.
import "../common/lib/wireErrors";

import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup, waitFor } from "@testing-library/react";

import type { CapabilitiesView } from "../wire";
import { jsonResponse, withQueryClient } from "@cove-extensions/ui-shared/testing";

// The seam is the host's own fetch, which the generated client reaches through the adapter in
// common/lib/wireClient, so what is scripted here is the request the card really issues.
const endpoint = vi.hoisted(() => ({
  // What the capabilities request answers with. Null until a test sets it, so an unconfigured call
  // fails loudly rather than resolving something nobody chose.
  respond: null as (() => Promise<Response>) | null,
}));

vi.mock("@cove/runtime/api", () => ({
  extensionFetch: (path: string) => {
    if (endpoint.respond === null) throw new Error(`unconfigured request to ${path}`);
    return endpoint.respond();
  },
}));

const { CapabilitiesCard } = await import("./CapabilitiesCard");

const answers = (view: CapabilitiesView) => () => jsonResponse(view);

const mount = () => render(withQueryClient(createElement(CapabilitiesCard)));

beforeEach(() => {
  endpoint.respond = null;
});

afterEach(cleanup);

test("a request still in flight shows the spinner and neither the values nor an error", async () => {
  endpoint.respond = () => new Promise<Response>(() => undefined);
  const { container } = mount();

  await waitFor(() => {
    expect(container.querySelector("svg.animate-spin")).not.toBeNull();
  });

  expect(container.textContent).not.toMatch(/couldn't read/i);
});

test("a rejected request shows the failure the request reported and no values", async () => {
  endpoint.respond = () => jsonResponse({ detail: "the host would not say" }, 403);
  const { container } = mount();

  expect(await screen.findByText(/couldn't read/i)).toBeDefined();
  expect(container.textContent).toContain("403 the host would not say");
  expect(container.querySelector("svg.animate-spin")).toBeNull();
});

test("a host that supplied everything renders nothing at all", async () => {
  endpoint.respond = answers({ coveVersion: "1.7.0", jobService: true, scanService: true });
  const { container } = mount();

  await waitFor(() => {
    expect(container.querySelector("svg.animate-spin")).toBeNull();
  });

  expect(container.querySelector("section")).toBeNull();
  expect(container.textContent).toBe("");
});

test.each([
  ["the scan service", true, false, "the scan service"],
  ["the job service", false, true, "the job service"],
  ["both services", false, false, "the job service or the scan service"],
])(
  "a host missing %s says so and names the version",
  async (_case, jobService, scanService, named) => {
    endpoint.respond = answers({ coveVersion: "1.7.0", jobService, scanService });
    const { container } = mount();

    expect(await screen.findByText(new RegExp(named))).toBeDefined();
    expect(container.textContent).toContain("a run cannot start");
    expect(container.textContent).toContain("1.7.0");
  },
);
