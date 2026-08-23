// The one JSON-over-HTTP client every caller in the harness uses to talk to a Cove instance.
//
// It lives in its own module rather than in fixtures.mjs so harness.mjs can use it: fixtures.mjs
// already imports harness.mjs, so owning the client there would close an import cycle.

/**
 * A `{get,post,put,delete}` JSON client over one Cove instance.
 *
 * Both `baseUrl` and `token` accept a value OR a getter, and the getter form is not a convenience.
 * `startHarness` documents that a restart re-mints the access token and MAY republish the container
 * on a new ephemeral host port, so a client that captured either one keeps addressing the old port
 * or presenting a credential the instance no longer accepts. Anything that outlives an install,
 * uninstall or restart must therefore pass `() => harness.baseUrl` and `() => harness.token`.
 *
 * A token is applied only when one is present. Against an auth-enabled instance every route answers
 * 401 without it; under the auth-off default the host's bypass principal ignores it — so passing it
 * is always correct and omitting it is correct only by luck.
 *
 * A body is sent whenever the caller supplies one, including `""`, `0` and `false`. Bodies are
 * JSON-encoded, which means a caller sending an already-stringified value (the extension data store
 * takes its blob as a STRING) gets the second encoding that endpoint expects.
 *
 * Never throws on an HTTP status: the status is returned for the caller to judge, so a polling loop
 * can retry one and a one-shot caller can raise its own error naming its own operation. Only a
 * transport failure (or an abort via `signal`) rejects.
 *
 * @param {string|(() => string)} baseUrl
 * @param {string|undefined|(() => string|undefined)} [token]
 */
export function createApiClient(baseUrl, token) {
  const resolveBase = () => (typeof baseUrl === "function" ? baseUrl() : baseUrl);
  const resolveToken = () => (typeof token === "function" ? token() : token);

  /**
   * @param {{signal?: AbortSignal}} [options] - `signal` bounds a single attempt, which a poll
   *   needs: the loop's own deadline is consulted only between attempts, and Node's fetch applies
   *   no timeout of its own.
   */
  async function call(method, path, body, { signal } = {}) {
    const hasBody = body !== undefined;
    const bearer = resolveToken();
    const res = await fetch(`${resolveBase()}${path}`, {
      method,
      headers: {
        ...(hasBody ? { "Content-Type": "application/json" } : {}),
        ...(bearer ? { Authorization: `Bearer ${bearer}` } : {}),
      },
      body: hasBody ? JSON.stringify(body) : undefined,
      signal,
    });
    const text = await res.text();
    let json;
    try {
      json = text ? JSON.parse(text) : undefined;
    } catch {
      json = undefined;
    }
    return { status: res.status, ok: res.ok, json, text };
  }

  return {
    /** The instance this client currently addresses, so a caller can name it in an error. */
    get baseUrl() {
      return resolveBase();
    },
    get: (path, options) => call("GET", path, undefined, options),
    post: (path, body, options) => call("POST", path, body, options),
    put: (path, body, options) => call("PUT", path, body, options),
    delete: (path, options) => call("DELETE", path, undefined, options),
  };
}
