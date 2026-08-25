// Ambient declarations for the host runtime modules Cove serves through its import map. Cove
// publishes no types for them, so each block below is transcribed from host source at the pinned
// release tag rather than inferred. Re-read the host source on every floor bump.
//
// Never add a top-level `import` or `export` here. Either one turns this file into a module, which
// silently demotes the blocks below from ambient declarations into augmentations of modules that do
// not otherwise exist.
//
// Keep the file free of module specifiers entirely. An `import type` INSIDE a block stays ambient but
// makes every declaration below hostage to that specifier resolving in each consuming program, and
// `skipLibCheck` suppresses errors in a `.d.ts`, so a consumer that cannot resolve it gets the
// imported type silently degraded to `any` rather than an error, and every call site then type-checks
// against nothing. Re-declare the shape you need instead.

declare module "@cove/runtime/api" {
  /**
   * Authenticated fetch for extension-owned, same-origin Cove API endpoints.
   *
   * Throws `TypeError` unless the URL is same-origin and its path is exactly `/api` or under `/api/`,
   * and forces `redirect: "error"` regardless of what `init` asks for; neither is readable off the
   * signature. Resolves for any HTTP status, so a caller must check `Response.ok` itself. A request
   * does not time out unless `timeoutMs` asks it to.
   */
  export function extensionFetch(
    input: string,
    init?: RequestInit & { timeoutMs?: number | null },
  ): Promise<Response>;
}
