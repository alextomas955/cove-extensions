// @vitest-environment jsdom
// The seam every request in the bundle goes through: the generated client calls one `fetch` with a
// built Request, and the host takes a path and an init. Each case drives a real generated operation
// so the adapter is exercised by the thing that will call it.
import { test, expect, vi, beforeEach, afterEach } from "vitest";

const host = vi.hoisted(() => ({
  calls: [] as { path: string; init: RequestInit }[],
  reply: null as Response | null,
}));

vi.mock("@cove/runtime/api", () => ({
  extensionFetch: (path: string, init: RequestInit) => {
    host.calls.push({ path, init });
    return Promise.resolve(host.reply ?? new Response("{}", { status: 200 }));
  },
}));

const { renamerGetJobStatus, renamerSaveOptions } = await import("../../wire/sdk.gen");
const { ApiError } = await import("@cove-extensions/ui-shared/extensionRequest");
await import("./wireErrors");

beforeEach(() => {
  host.calls.length = 0;
  host.reply = null;
});

afterEach(() => {
  vi.useRealTimers();
});

test("a request reaches the host as a same-origin path, with no origin in front of it", async () => {
  host.reply = new Response(JSON.stringify({ id: "j1" }), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });

  await renamerGetJobStatus({ path: { jobId: "j1" } });

  expect(host.calls).toHaveLength(1);
  // The client builds an absolute url against the document origin; extensionFetch refuses anything
  // outside /api/, so what it is handed must start there.
  expect(host.calls[0].path).toBe("/api/extensions/com.alextomas955.renamer/job-status/j1");
  expect(host.calls[0].init.method).toBe("GET");
});

test("a body survives being taken off the Request the client built", async () => {
  host.reply = new Response(null, { status: 204 });

  await renamerSaveOptions({ body: { filenameTemplate: "$title" } });

  expect(host.calls).toHaveLength(1);
  expect(host.calls[0].init.method).toBe("PUT");
  expect(host.calls[0].init.body).toBe(JSON.stringify({ filenameTemplate: "$title" }));
});

test("a failed request arrives as an ApiError carrying the status the body does not", async () => {
  host.reply = new Response("scan superseded", { status: 404, statusText: "Not Found" });

  const err = await renamerGetJobStatus({ path: { jobId: "gone" } }).then(
    () => null,
    (e: unknown) => e,
  );

  expect(err, "a 404 resolved instead of throwing").toBeInstanceOf(ApiError);
  expect((err as InstanceType<typeof ApiError>).status).toBe(404);
  expect((err as InstanceType<typeof ApiError>).body).toContain("superseded");
});
