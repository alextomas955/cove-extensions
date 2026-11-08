/**
 * Stands in for what the host serves at `@cove/runtime/api`, aliased in by a vitest project so a test
 * can render a real component that reaches it. Always rejects: there is no Cove behind a unit run.
 */
/** @public */
export function extensionFetch(): Promise<Response> {
  return Promise.reject(new Error("no Cove host in a unit run"));
}
