/// <reference path="./coveRuntime.d.ts" />
// Public surface of the shared UI module, consumed by each extension bundle through its
// `@cove-extensions/ui-shared` alias. Consumers import from this barrel; intra-module files import each
// other by relative path so the pure-logic modules stay independently compilable by the offline
// check runners.
export * from "./primitives";
export * from "./overlay";
export * from "./entityPickerLogic";
// `actions` is pure (zero-import), so re-exporting it here keeps the barrel offline-gate-safe. The
// SDK-touching `postAction` is deliberately NOT re-exported — it is imported from its own subpath so
// the gates that compile this barrel never resolve `@cove/extension-sdk`.
export * from "./actions";
// Re-export the pure logic functions explicitly: `primitivesLogic` also declares a `RegexValidity`
// result interface whose name coincides with the `RegexValidity` presentational component in
// `primitives`, so a blanket `export *` would collide. Consumers use the component by that name; the
// result interface stays internal to the module (it is only `isRegexValid`'s return shape).
export {
  filterByText,
  isRegexValid,
  isAbsolutePathShape,
  extensionShapeAdvisory,
} from "./primitivesLogic";
