// @vitest-environment jsdom
import { test, expect, vi, beforeEach } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";
import type { RunOptions } from "./runOptions";

const server = vi.hoisted(() => ({
  // What the options read answers with. Null until a test sets it, so an unconfigured call fails
  // loudly rather than resolving something nobody chose.
  respond: null as (() => Promise<RunOptions>) | null,
  writes: [] as { path: string; method: string | undefined; body: string | undefined }[],
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  errorText: (err: unknown) => (err instanceof Error ? err.message : String(err)),
  requestJson: (path: string) => {
    if (server.respond === null) throw new Error(`unconfigured request to ${path}`);
    return server.respond();
  },
  request: (path: string, options: RequestInit) => {
    server.writes.push({
      path,
      method: options.method,
      body: typeof options.body === "string" ? options.body : undefined,
    });
    return Promise.resolve(undefined);
  },
}));

const { RunOptionsForm } = await import("./RunOptionsForm");

function mount(): HTMLElement {
  const container = document.createElement("div");
  document.body.append(container);
  createRoot(container).render(createElement(RunOptionsForm));
  return container;
}

const toggle = (container: HTMLElement) =>
  container.querySelector<HTMLButtonElement>('button[role="switch"]');

const button = (container: HTMLElement, text: string) =>
  [...container.querySelectorAll("button")].find((e) => e.textContent.trim() === text);

// A whole record, the way the route answers one. Copied from the C# record's declared defaults, so a
// field the server stops writing shows up here as a type error rather than as an invented value.
const answered = (overrides: Partial<RunOptions> = {}): RunOptions => ({
  thumbnails: true,
  previews: true,
  sprites: true,
  vrStereo: false,
  imageThumbnails: true,
  galleryThumbnails: true,
  backfillFingerprints: true,
  rescan: false,
  createMissingEntities: true,
  markOrganized: false,
  ...overrides,
});

beforeEach(() => {
  server.respond = null;
  server.writes = [];
  document.body.replaceChildren();
});

test("a read still in flight shows the spinner and no switch", async () => {
  server.respond = () => new Promise<RunOptions>(() => undefined);
  const container = mount();

  await waitFor(
    "the spinner to render",
    () => container.querySelector("svg.animate-spin") !== null,
  );

  expect(toggle(container)).toBeNull();
});

test("a settled read shows the switch at the value the server answered", async () => {
  server.respond = () => Promise.resolve(answered({ backfillFingerprints: false }));
  const container = mount();

  await waitFor("the switch to render", () => toggle(container) !== null);

  expect(toggle(container)?.getAttribute("aria-checked")).toBe("false");
  expect(button(container, "Save")?.disabled).toBe(true);
});

test("toggling the switch enables Save, and Save issues one PUT carrying the edited value", async () => {
  server.respond = () => Promise.resolve(answered());
  const container = mount();
  await waitFor("the switch to render", () => toggle(container) !== null);

  toggle(container)?.click();
  await waitFor("Save to become enabled", () => button(container, "Save")?.disabled === false);
  button(container, "Save")?.click();
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
  expect(toggle(container)).toBeNull();
});
