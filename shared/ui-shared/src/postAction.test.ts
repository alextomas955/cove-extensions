// Drives the real request chain - extensionFetch, send, request - with only the host's fetch
// replaced, so a malformed body is parsed by the same JSON.parse the browser would reach and an
// abort is the rejection a real cancellation produces.
import { beforeEach, expect, test, vi } from "vitest";

const fetchMock = vi.hoisted(() => vi.fn<() => Promise<Response>>());

vi.mock("@cove/runtime/api", () => ({ extensionFetch: fetchMock }));

const { postAction } = await import("./postAction");
const { ApiError } = await import("./extensionRequest");

function answer(status: number, body: string): Response {
  return new Response(body, {
    status,
    headers: body ? { "Content-Type": "application/json" } : {},
  });
}

beforeEach(() => {
  fetchMock.mockReset();
});

test("a bodyless 2xx resolves undefined, and the body it posted is the one it was given", async () => {
  fetchMock.mockResolvedValue(new Response(null, { status: 202 }));

  await expect(postAction("/extensions/x/renamer", { entityIds: [1, 2] })).resolves.toBeUndefined();

  const [path, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
  expect(path).toBe("/api/extensions/x/renamer");
  expect(init.method).toBe("POST");
  expect(init.body).toBe('{"entityIds":[1,2]}');
});

test("a 2xx carrying JSON resolves that body", async () => {
  fetchMock.mockResolvedValue(answer(200, '{"jobId":"abc"}'));

  await expect(postAction<{ jobId: string }>("/extensions/x/renamer")).resolves.toEqual({
    jobId: "abc",
  });
});

test("a non-ok status rejects with ApiError carrying the status and body", async () => {
  fetchMock.mockResolvedValue(answer(403, "forbidden"));

  const err = await postAction("/extensions/x/renamer").catch((e: unknown) => e);
  expect(err).toBeInstanceOf(ApiError);
  expect((err as InstanceType<typeof ApiError>).status).toBe(403);
});

// The three below are what the old broad catch resolved as success. Each one means the server did
// not accept the action, so reporting it as done is the defect.
test("a network failure rejects rather than resolving", async () => {
  fetchMock.mockRejectedValue(new TypeError("Failed to fetch"));

  await expect(postAction("/extensions/x/renamer")).rejects.toThrow("Failed to fetch");
});

test("a cancelled request rejects rather than resolving", async () => {
  fetchMock.mockRejectedValue(new DOMException("The operation was aborted.", "AbortError"));

  const err = await postAction("/extensions/x/renamer").catch((e: unknown) => e);
  expect((err as DOMException).name).toBe("AbortError");
});

test("a 2xx whose body is not JSON rejects rather than resolving", async () => {
  fetchMock.mockResolvedValue(answer(200, "<!doctype html><html>"));

  await expect(postAction("/extensions/x/renamer")).rejects.toThrow(SyntaxError);
});
