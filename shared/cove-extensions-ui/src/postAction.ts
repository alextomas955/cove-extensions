// Not in the barrel: the offline gates compile the barrel with `types: []` and would fail to resolve
// this `@cove/extension-sdk` import. Consumers reach it via the `./postAction` subpath instead.
import { request, ApiError } from "@cove/extension-sdk";

/**
 * POST an extension route. Contract: a real {@link ApiError} is rethrown (the host's onError alert
 * shows it); a non-ApiError raised after a 2xx — the SDK's `res.json()` throw on an empty response
 * body — resolves `{}` as success. Background-job routes answer with a real body (a {@link QueuedJob})
 * that is returned as-is.
 */
export async function postAction<T extends object = Record<string, never>>(
  path: string,
  body?: unknown,
): Promise<T | Record<string, never>> {
  try {
    return await request<T>(path, {
      method: "POST",
      body: JSON.stringify(body),
    });
  } catch (err) {
    if (err instanceof ApiError) throw err;
    return {};
  }
}
