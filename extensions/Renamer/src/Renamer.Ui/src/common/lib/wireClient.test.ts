// @vitest-environment jsdom
// The seam every request in the bundle goes through: the generated client calls one `fetch` with a
// built Request, and the host takes a path and an init. Each case drives a real generated operation
// so the adapter is exercised by the thing that will call it.
import { test, expect, vi, beforeEach, afterEach } from "vitest";

const host = vi.hoisted(() => ({
  calls: [] as { path: string; init: RequestInit }[],
  reply: null as Response | null,
  // Set to fail the request before any reply, the way a dropped connection or an abort does.
  rejectWith: null as Error | null,
}));

vi.mock("@cove/runtime/api", () => ({
  extensionFetch: (path: string, init: RequestInit) => {
    host.calls.push({ path, init });
    if (host.rejectWith) return Promise.reject(host.rejectWith);
    return Promise.resolve(host.reply ?? new Response("{}", { status: 200 }));
  },
}));

const { renamerGetJobStatus, renamerSaveOptions, renamerUndo } = await import("../../wire/sdk.gen");
const { ApiError, isRefusal } = await import("@cove-extensions/ui-shared/extensionRequest");
await import("./wireErrors");

// Drive one real generated operation and hand back whatever it failed with.
function failureOf(call: Promise<unknown>): Promise<unknown> {
  return call.then(
    () => null,
    (e: unknown) => e,
  );
}

beforeEach(() => {
  host.calls.length = 0;
  host.reply = null;
  host.rejectWith = null;
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

test("a problem body reaches the reader as its detail, not as the envelope", async () => {
  const { errorText } = await import("@cove-extensions/ui-shared/extensionRequest");
  host.reply = new Response(
    JSON.stringify({
      type: "urn:cove:extension:error:MIGRATION_PENDING",
      title: "Conflict",
      status: 409,
      detail: "A one-time conversion of the stored settings has not run yet.",
      code: "MIGRATION_PENDING",
    }),
    { status: 409, headers: { "Content-Type": "application/problem+json" } },
  );

  const err = await renamerSaveOptions({ body: {}, throwOnError: true }).then(
    () => null,
    (e: unknown) => e,
  );

  // The sentence the server wrote for a reader, and nothing else from the envelope.
  expect(errorText(err)).toBe("409 A one-time conversion of the stored settings has not run yet.");
});

test("a route answering something other than a problem still reads as what it sent", async () => {
  const { errorText } = await import("@cove-extensions/ui-shared/extensionRequest");
  host.reply = new Response("upstream exploded", { status: 502, statusText: "Bad Gateway" });

  const err = await failureOf(renamerSaveOptions({ body: {}, throwOnError: true }));

  expect(errorText(err)).toBe("502 upstream exploded");
});

// The one the client cannot tell apart on its own: it runs the whole request in a single `try`, so
// a body that will not parse surfaces beside the 200 the server already sent. Nothing downstream
// could recover the difference, so the boundary has to keep it.
test("a 2xx reply the client could not read is not an answer the server gave", async () => {
  host.reply = new Response("{ not json", {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });

  const err = await failureOf(renamerUndo({ throwOnError: true }));

  expect(err, "an unreadable 200 arrived as a status the server chose").not.toBeInstanceOf(
    ApiError,
  );
  expect(isRefusal(err), "an unreadable 200 read as a refusal").toBe(false);
  // Says a reply came back and could not be read, which is the one fact that separates this from a
  // request that never arrived, and keeps the parser's own reason.
  expect((err as Error).message).toContain("reply could not be read");
  expect((err as Error).message).toMatch(/JSON/);
});

test("a server that failed while working is not a refusal", async () => {
  host.reply = new Response("boom", { status: 500, statusText: "Internal Server Error" });

  const err = await failureOf(renamerUndo({ throwOnError: true }));

  // Still an ApiError: the status is real and a reader is shown it. It is the claim that the work
  // did not begin that a 500 cannot support.
  expect(err).toBeInstanceOf(ApiError);
  expect((err as InstanceType<typeof ApiError>).status).toBe(500);
  expect(isRefusal(err), "a 500 read as a refusal").toBe(false);
});

test("a refusal the server stated is one", async () => {
  host.reply = new Response("forbidden", { status: 403, statusText: "Forbidden" });

  const err = await failureOf(renamerUndo({ throwOnError: true }));

  expect(isRefusal(err), "a 403 did not read as a refusal").toBe(true);
});

test("a request that never reached the server carries no status", async () => {
  host.rejectWith = new TypeError("Failed to fetch");

  const err = await failureOf(renamerUndo({ throwOnError: true }));

  expect(err).toBe(host.rejectWith);
  expect(isRefusal(err), "a dropped connection read as a refusal").toBe(false);
});
