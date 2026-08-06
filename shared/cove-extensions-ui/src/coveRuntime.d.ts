// Ambient declarations for the host runtime modules Cove serves through its import map. Cove
// publishes no types for them, so each block below is transcribed from host source at the pinned
// release tag rather than inferred — and remains an assertion nothing in this repo can check until
// the bundle loads in a real host. Re-read the host source on every floor bump.
//
// Kept in the shared package and reached by a single triple-slash reference from its barrel, which
// every extension already imports: the declarations arrive in each extension's program with no
// per-extension tsconfig edit.
//
// Never add a top-level `import` or `export` here. Either one turns this file into a module, which
// silently demotes the blocks below from ambient declarations into augmentations of modules that do
// not otherwise exist.

declare module "@cove/runtime/api" {
  /**
   * Authenticated fetch for extension-owned, same-origin Cove API endpoints.
   *
   * Throws `TypeError` unless the URL is same-origin and its path is exactly `/api` or under `/api/`,
   * and forces `redirect: "error"` regardless of what `init` asks for — neither is readable off the
   * signature. Resolves for any HTTP status, so a caller must check `Response.ok` itself.
   * Host-provided at Cove >= 1.1.0.
   */
  export function extensionFetch(
    input: string,
    init?: RequestInit,
  ): Promise<Response>;
}

// Empty body on purpose. The host barrel behind this specifier exports roughly sixty values, and
// transcribing them without a consumer would be invention. Declared-but-empty keeps the module
// resolvable, so a premature import fails as TS2305 (no exported member) rather than TS2307 (cannot
// find module) — the error then names the real problem.
//
// Adding the first symbol: add it together with its first consumer, transcribed from host source at
// the pinned tag. Where the vendored `@cove/extension-sdk` already ships the type, import it rather
// than re-declaring it — as `import type` (`verbatimModuleSyntax` is on) and written INSIDE this
// block, since a top-level import would un-ambient the whole file.
declare module "@cove/runtime/components" {}
