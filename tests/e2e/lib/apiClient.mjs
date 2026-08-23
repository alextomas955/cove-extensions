// The one JSON-over-HTTP client every caller in the harness uses to talk to a Cove instance.
//
// It lives here rather than in fixtures.mjs because that module already imports harness.mjs, which
// needs the client too, so owning it there would close an import cycle.

/**
 * A `{get,post,put,delete}` JSON client over one Cove instance.
 *
 * Both `baseUrl` and `token` accept a value OR a getter. A restart re-mints the access token and MAY
 * republish the container on a new ephemeral host port, so a client that captured either one goes on
 * addressing the old port or presenting a credential the instance no longer accepts. Anything that
 * outlives an install, uninstall or restart must pass `() => harness.baseUrl` and
 * `() => harness.token`.
 *
 * A token is applied only when one is present. An auth-enabled instance answers 401 on every route
 * without it; under the auth-off default the host's bypass principal ignores it.
 *
 * A body is sent whenever the caller supplies one, including `""`, `0` and `false`. Bodies are
 * JSON-encoded, so a caller sending an already-stringified value (the extension data store takes its
 * blob as a STRING) gets the second encoding that endpoint expects.
 *
 * Never throws on an HTTP status; the status is returned for the caller to judge. Only a transport
 * failure, or an abort via `signal`, rejects.
 *
 * @param {string|(() => string)} baseUrl
 * @param {string|undefined|(() => string|undefined)} [token]
 */
export function createApiClient(baseUrl, token) {
  const resolveBase = () => (typeof baseUrl === "function" ? baseUrl() : baseUrl);
  const resolveToken = () => (typeof token === "function" ? token() : token);

  /** @param {{signal?: AbortSignal}} [options] - `signal` bounds this single call. */
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
    /** Re-resolved on every read, so it follows a port a restart republished. */
    get baseUrl() {
      return resolveBase();
    },
    get: (path, options) => call("GET", path, undefined, options),
    post: (path, body, options) => call("POST", path, body, options),
    put: (path, body, options) => call("PUT", path, body, options),
    delete: (path, options) => call("DELETE", path, undefined, options),
  };
}
