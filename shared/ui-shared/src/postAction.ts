// Not in the barrel: this module reaches the host runtime and the SDK's error class through
// `./extensionRequest`, and keeping it behind the `./postAction` subpath is what lets a consumer
// import the barrel without taking either.
import { request } from "./extensionRequest";

/**
 * POST an extension route, resolving the parsed response body, or `undefined` for the bodyless 2xx
 * that a fire-and-forget route answers with.
 *
 * `request` rather than `requestJson`, because an empty body is such a route's success and not its
 * failure. Nothing is caught: an {@link ApiError} for a non-ok status, a network or cancellation
 * failure, and a malformed 2xx body all reach the caller, which is where the host's onError alert
 * reads them. Catching the last three and resolving a value instead would report an action the
 * server never accepted as done.
 */
export function postAction<T extends object = Record<string, never>>(
  path: string,
  body?: unknown,
): Promise<T | undefined> {
  return request<T>(path, { method: "POST", body: JSON.stringify(body) });
}
