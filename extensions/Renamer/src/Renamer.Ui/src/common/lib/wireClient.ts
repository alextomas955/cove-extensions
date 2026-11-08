/**
 * The generated client's runtime configuration, named by `runtimeConfigPath` in the wire generator
 * and called once as that client is created.
 *
 * It exists to bind the host's `extensionFetch` in. A bare `fetch` carries no credential, so every
 * request would answer 401 on an instance with auth enabled, and the generated client has no way to
 * know. Nothing generated ever reaches `fetch` itself.
 */
import { extensionFetch } from "@cove/runtime/api";

import type { CreateClientConfig } from "../../wire/client.gen";

/**
 * Adapts the host's path-and-init fetch to the one-Request shape the generated client calls.
 *
 * The client hands its `fetch` a built `Request`; `extensionFetch` takes a same-origin path and an
 * init, and refuses anything outside `/api/`. So the request is taken apart here: the origin the
 * Request resolved against is dropped, and the body is read out, which is why this is async.
 */
const fetchThroughHost: typeof fetch = async (input, init) => {
  const request = input instanceof Request ? input : new Request(input, init);
  const { pathname, search } = new URL(request.url);

  // A GET or HEAD Request carries no body, and reading one that does not exist throws.
  const body =
    request.method === "GET" || request.method === "HEAD" ? undefined : await request.text();

  return extensionFetch(pathname + search, {
    method: request.method,
    headers: request.headers,
    body,
    // Carried through so a query that unmounts aborts its request rather than resolving into a
    // component that is gone.
    signal: request.signal,
  });
};

// `location` is typed as always present, and in a browser it is. A test in the node environment has
// none, and only the parseability of this value matters, so the absence is read at runtime.
const ORIGIN = typeof location === "undefined" ? "http://extension.invalid" : location.origin;

export const createClientConfig: CreateClientConfig = (config) => ({
  ...config,
  // The operation urls already carry `/api`, which is also the only prefix `extensionFetch` accepts,
  // so this adds an origin and nothing else. It is there because the client builds a `Request`, and
  // that constructor refuses a relative url outside a browser. The adapter above drops the origin
  // again, so only its parseability matters and what reaches the host is the same path either way -
  // which is why a run with no `location`, such as a test in the node environment, can name one.
  baseUrl: ORIGIN,
  fetch: fetchThroughHost,
  // The repo's error contract is a throw, which is also what `useQuery` needs to mark a query
  // failed. The returned-union style would leave a failed request looking like a resolved one.
  throwOnError: true,
});
