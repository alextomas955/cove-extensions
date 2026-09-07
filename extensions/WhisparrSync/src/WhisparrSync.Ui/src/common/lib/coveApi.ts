/**
 * The one transport this bundle talks to Cove through.
 *
 * It exists because the SDK's `request()` calls a bare `fetch()`, which authenticates by COOKIE only.
 * A real browser session carries the cookie, so that worked — but any caller holding a bearer token and
 * no cookie (an automated drive, an embedded view) arrives anonymous and every route answers 401.
 * `extensionFetch` is the host's own authenticated fetch: it attaches the bearer, retries once after a
 * token refresh, and refuses a url that is not a same-origin `/api` path.
 *
 * The signature and the thrown `ApiError` are deliberately unchanged from the SDK's, so call sites read
 * the same and an `instanceof ApiError` check stays true across the whole bundle — there is exactly one
 * error type, re-exported from the SDK rather than redefined here.
 */
import { ApiError } from "@cove/extension-sdk";
import { extensionFetch } from "@cove/runtime/api";

export { ApiError };

const ApiRoot = "/api";

/** Issue a JSON request against a Cove API path (`/extensions/…`), resolving the parsed body. */
export async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  // Built through Headers rather than an object spread: HeadersInit is legally a Headers instance or an
  // array of pairs, both of which a spread would silently mangle. It also lets a caller's own
  // Content-Type win instead of leaving that to key order.
  const headers = new Headers(options.headers);
  if (!headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }

  const response = await extensionFetch(`${ApiRoot}${path}`, { ...options, headers });

  if (!response.ok) {
    const body = await response.text().catch(() => "");
    throw new ApiError(response.status, body || response.statusText, path);
  }

  // 204 carries no body; `T` is `void` at those call sites.
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}
