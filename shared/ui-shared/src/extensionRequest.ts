// Not in the barrel: this module reaches the host runtime AND the SDK, and keeping it behind the
// `./extensionRequest` subpath is what lets a consumer import the barrel without taking either.
import { extensionFetch } from "@cove/runtime/api";
import { ApiError } from "@cove/extension-sdk";

// Re-exported so every `instanceof ApiError` branch keeps testing the SDK's own class.
export { ApiError };

/**
 * Sends one request through the host's authenticated fetch, which attaches the signed-in user's
 * credential and constrains the request to same-origin `/api`, and returns the raw response text
 * alongside its status. Any non-ok response raises {@link ApiError}, carrying status, response body
 * and the unprefixed path.
 */
async function send(path: string, options: RequestInit): Promise<{ status: number; body: string }> {
  // The `/api` prefix belongs here rather than at each call site: `extensionFetch` throws a
  // TypeError for a path outside `/api/`, and a TypeError is not an ApiError, so a mis-prefixed
  // path falls through every `instanceof ApiError` branch into the generic string-error arm and
  // reads as an unrelated failure.
  //
  // `HeadersInit` is a union whose arms do not survive an object spread: spreading a `Headers`
  // instance yields `{}` (its entries are internal, not own enumerable properties) and spreading the
  // entry-array form yields index keys, so in both cases the caller's headers vanish with no error.
  // Going through `Headers` also collapses the casing, so a caller passing a lowercase
  // `content-type` overrides the default instead of producing both spellings, which `fetch` would
  // send as one comma-joined value.
  const headers = new Headers(options.headers);
  if (!headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }

  const res = await extensionFetch(`/api${path}`, { ...options, headers });

  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new ApiError(res.status, text || res.statusText, path);
  }

  // A 204's body is null and reads as "", the same as the bodyless 200 the host answers its
  // extension-data PUT with: one empty-body case for the two functions below to decide over, not a
  // status test each would have to repeat.
  return { status: res.status, body: await res.text() };
}

/**
 * Request an extension route that may legitimately answer with no body, resolving `undefined` in
 * that case: an empty body is a success here, not a parse error, and handing it to the JSON parser
 * would raise a SyntaxError a caller could only read as failure.
 */
export async function request<T>(path: string, options: RequestInit = {}): Promise<T | undefined> {
  const { body } = await send(path, options);
  return body ? (JSON.parse(body) as T) : undefined;
}

/**
 * Request an extension route that must answer with a JSON body: identical signature and `/api`
 * prefixing to {@link request}, and the same {@link ApiError} raised for a non-ok response.
 *
 * An empty body raises {@link ApiError} too, rather than resolving a value the declared type forbids.
 */
export async function requestJson<T>(path: string, options: RequestInit = {}): Promise<T> {
  const { status, body } = await send(path, options);
  if (!body) {
    throw new ApiError(status, "response carried no body, but JSON was expected", path);
  }
  return JSON.parse(body) as T;
}
