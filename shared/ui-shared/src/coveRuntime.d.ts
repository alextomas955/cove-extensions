// Ambient declarations for the host runtime modules Cove serves through its import map. Cove
// publishes no types for them, so each block below is transcribed from host source at the pinned
// release tag, and remains an assertion nothing in this repo can check until the bundle loads in a
// real host. Re-read the host source on every floor bump.
//
// Kept in the shared package and reached by a single triple-slash reference from its barrel, which
// every extension already imports: the declarations arrive in each extension's program with no
// per-extension tsconfig edit.
//
// Never add a top-level `import` or `export` here. Either one turns this file into a module, which
// silently demotes the blocks below from ambient declarations into augmentations of modules that do
// not otherwise exist.
//
// Keep the file free of module specifiers entirely. An `import type` INSIDE a block stays ambient but
// makes every declaration below hostage to that specifier resolving in each consuming program. There
// is no `node_modules` beside this file, so it would resolve only through a consumer's tsconfig
// `paths`; and because `skipLibCheck` suppresses errors in a `.d.ts`, a consumer lacking that entry
// gets the imported type silently degraded to `any` rather than an error, and every prop on every
// call site then type-checks against nothing. Re-declare the shape you need instead.
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


// The host barrel behind this specifier exports far more than this, so a symbol is added here
// together with the code that imports it, narrowed to the surface that consumer actually passes. An
// undeclared symbol then fails as TS2305 (no exported member) rather than TS2307 (cannot find
// module), which names the real problem.
//
// Nothing in this repo can check that these prop shapes match what the host actually renders: the
// type-check only confirms call sites agree with this file, and would agree just as happily with a
// wrong transcription. The containerized e2e spec, which loads a built bundle into a real host, is
// what proves them.
declare module "@cove/runtime/components" {
  // Narrowed from the host's derived `Extract<CustomFieldType, …> | "face"` to the three kinds this
  // repo selects over; the derived form would drag in a host type nothing here declares.
  export type EntityReferenceType = "tag" | "performer" | "studio";

  // Each component is a bare call signature rather than `ComponentType<P>`, which would need react
  // (see the header). The props ARE the contract and are checked at every call site; the rendered
  // result is only ever consumed by JSX, so the return is left uninhabited rather than restating
  // React's node union: `never` satisfies the JSX element check without naming a react type.
  export const EntityReferenceMultiSelector: (props: {
    entityType: EntityReferenceType;
    values: number[];
    onChange: (values: number[]) => void;
    placeholder?: string;
    excludeIds?: Iterable<number>;
    allowCreate?: boolean;
    inputClassName?: string;
  }) => never;

  export const EntityReferenceValue: (props: {
    entityType: EntityReferenceType;
    value: unknown;
  }) => never;
}
