// Not in the barrel: this module reaches the host runtime AND the SDK, and keeping it behind the
// `./extensionRequest` subpath is what lets a consumer import the barrel without taking either.
import { extensionFetch } from "@cove/runtime/api";
import { ApiError } from "@cove/extension-sdk";

// Re-exported so a call site that needs both the request and its error class changes exactly one
// import line, and so every `instanceof ApiError` branch keeps testing the SDK's own class.
export { ApiError };

/**
 * Request an extension route through the host's authenticated fetch, which attaches the signed-in
 * user's credential and constrains the request to same-origin `/api`.
 *
 * A drop-in for the SDK's `request<T>(path, options?)`: identical signature, identical `/api`
 * prefixing, and the same {@link ApiError} — carrying status, response body and the unprefixed path
 * — thrown for any non-ok response. Resolves `undefined` when the response carries no body.
 */
export async function request<T>(
  path: string,
  options: RequestInit = {},
): Promise<T> {
  // The `/api` prefix belongs here rather than at each call site: `extensionFetch` throws a
  // TypeError for a path outside `/api/`, and a TypeError is not an ApiError, so a mis-prefixed
  // path falls through every `instanceof ApiError` branch into the generic string-error arm and
  // reads as an unrelated failure.
  const res = await extensionFetch(`/api${path}`, {
    ...options,
    headers: { "Content-Type": "application/json", ...options.headers },
  });

  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new ApiError(res.status, text || res.statusText, path);
  }

  // An empty body is a success, not a parse error. The host answers its extension-data PUT with 200
  // and no body at all; handing that to the JSON parser raises a SyntaxError that a caller can only
  // read as failure.
  if (res.status === 204) return undefined as T;
  const body = await res.text();
  return (body ? JSON.parse(body) : undefined) as T;
}
