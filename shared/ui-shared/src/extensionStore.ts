// Not in the barrel: this module reaches the host runtime through `./extensionRequest`, so it is
// kept behind its own `./extensionStore` subpath for the same reason that module is.
import { request, requestJson } from "./extensionRequest";

/** Scoped key-value storage for one extension, over the two routes the host actually serves. */
export interface ExtensionDataStore {
  /**
   * Every stored key. A key that was never written reads as `undefined` at runtime, which the value
   * type states so a consumer's missing-key guard stays meaningful rather than being typed as dead.
   */
  getAll: () => Promise<Record<string, string | undefined>>;
  /** Stores `value` as this extension's `key`, serialized as JSON. */
  set: (key: string, value: unknown) => Promise<void>;
}

/**
 * Binds the host's extension data store to one extension id.
 *
 * Two methods, because the host serves two routes: the collection `GET /api/extensions/{id}/data`
 * and the per-key `PUT /api/extensions/{id}/data/{key}`. There is no per-key GET, no collection
 * POST and no DELETE; a store offering those targets routes that do not exist.
 */
export function createExtensionDataStore(extensionId: string): ExtensionDataStore {
  const base = `/extensions/${extensionId}/data`;

  return {
    getAll: () => requestJson<Record<string, string | undefined>>(base),

    set: async (key, value) => {
      await request<unknown>(`${base}/${encodeURIComponent(key)}`, {
        method: "PUT",
        // Double serialize: the inner one produces the stored value, the outer one makes the HTTP
        // body a JSON string literal, which is what the route's `[FromBody] string value` binds.
        body: JSON.stringify(JSON.stringify(value)),
      });
    },
  };
}
