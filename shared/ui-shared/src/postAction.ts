// Not in the barrel: this module reaches the host runtime and the SDK's error class through
// `./extensionRequest`, and keeping it behind the `./postAction` subpath is what lets a consumer
// import the barrel without taking either.
import { request, ApiError } from "./extensionRequest";

/**
 * POST an extension route. Contract: a real {@link ApiError} is rethrown (the host's onError alert
 * shows it); an empty 2xx response body resolves `{}` as success, as does any other non-ApiError
 * raised after a 2xx. Background-job routes answer with a real body (a {@link QueuedJob}) that is
 * returned as-is.
 */
export async function postAction<T extends object = Record<string, never>>(
  path: string,
  body?: unknown,
): Promise<T | Record<string, never>> {
  try {
    // The coalesce is load-bearing, not defensive: the /renamer response IS the bodyless 2xx that
    // `request` resolves `undefined` for, so this is the live path here. `request` rather than
    // `requestJson` for exactly that reason: an empty body is this route's success, not its failure.
    const res = await request<T>(path, {
      method: "POST",
      body: JSON.stringify(body),
    });
    return res ?? {};
  } catch (err) {
    if (err instanceof ApiError) throw err;
    return {};
  }
}
