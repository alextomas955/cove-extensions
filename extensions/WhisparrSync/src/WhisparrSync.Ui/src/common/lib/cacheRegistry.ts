/**
 * A connection-scoped cache-invalidation registry that inverts the settings→slice coupling: instead of the
 * settings slice importing each sibling slice's clear function (a cross-slice edge the boundaries policy
 * forbids), every status store self-registers its clear callback here at module load, and the settings page
 * calls one aggregate. The registry holds only `() => void` callbacks and imports no slice, so the dependency
 * runs slice→common in both directions.
 *
 * Registration is module-load-order tolerant by design: a cache is populated only if its store module was
 * evaluated, and evaluating that module also runs its self-registration — so every populated cache is
 * registered and cleared, while a store not yet loaded has no cache to clear and is simply absent from the list.
 */

/**
 * Invalidate a connection-scoped read cache when the Whisparr connection/settings change. Each callback MUST
 * be an idempotent no-op on a never-populated cache (resetting its own map/state), so it is safe to invoke
 * regardless of whether its slice has fetched anything yet.
 */
type ConnectionScopedClear = () => void;

const registered: ConnectionScopedClear[] = [];

/** Register a store's connection-scoped clear callback. Called once per store at module-evaluation time. */
export function registerConnectionScopedCache(clear: ConnectionScopedClear): void {
  registered.push(clear);
}

/**
 * Invalidate every registered connection-scoped cache. Runs each callback synchronously and in registration
 * order — callers (the settings save) depend on the caches being empty immediately after this returns, so this
 * must never become async or debounced (that would change clear timing = a behavior change).
 */
export function clearConnectionScopedCaches(): void {
  for (const clear of registered) {
    clear();
  }
}
