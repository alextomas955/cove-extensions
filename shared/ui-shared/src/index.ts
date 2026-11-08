// Public surface of the shared UI module, consumed by each extension bundle through its
// `@cove-extensions/ui-shared` alias. Consumers import from this barrel; intra-module files import each
// other by relative path so the pure-logic modules stay independently importable.
export * from "./fields";
export * from "./chips";
export * from "./collectionEditors";
export * from "./layout";
export * from "./feedback";
export * from "./overlay";
// `actions` is pure (zero-import), so re-exporting it here costs a consumer nothing. The SDK-touching
// `postAction` is deliberately not re-exported - it is reached through its own `./postAction` subpath,
// so importing this barrel never pulls `@cove/extension-sdk` into the consumer's graph.
export * from "./actions";
// Re-exported by name: `primitivesLogic` also declares a `RegexValidity` result interface whose name
// coincides with the `RegexValidity` presentational component in `feedback`, so a blanket `export *`
// would collide. Consumers use the component by that name.
export { extensionShapeAdvisory, type ValueOption } from "./primitivesLogic";
