/**
 * What the host serves for `@cove/runtime/api`, for a test that renders a real component reaching
 * it, aliased in by a vitest project. Rejects: there is no Cove behind a unit run, and a hook reading this endpoint is written to
 * fall back rather than to accuse every rule of being broken when the read fails.
 */
export function extensionFetch(): Promise<Response> {
  return Promise.reject(new Error("no Cove host in a unit run"));
}
