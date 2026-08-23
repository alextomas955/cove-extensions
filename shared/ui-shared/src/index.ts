// Public surface of the shared UI module, consumed by each extension bundle through its
// `@cove-extensions/ui-shared` alias. Consumers import from this barrel; intra-module files import each
// other by relative path so the pure-logic modules stay independently importable.
export * from "./primitives";
export * from "./overlay";
export * from "./entityPickerLogic";
// `actions` is pure (zero-import), so re-exporting it here costs a consumer nothing. The SDK-touching
// `postAction` is deliberately NOT re-exported — it is reached through its own `./postAction` subpath,
// so importing this barrel never pulls `@cove/extension-sdk` into the consumer's graph.
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
