// The replies a fake `extensionFetch` hands back. The generated client reads a reply by its
// Content-Type, so a fake that answers with a bare Response and no header takes a different path
// through the client than the server does.
//
// Which of the two a case wants is stated at the call site rather than inferred from the body's
// type: a string body is a reply that parses to a string under one and a reply that does not parse
// at all under the other, and the client's error interceptor tells those apart.

/** A reply the client can parse. `undefined` sends no body, which is what a 204 answers. */
export function jsonResponse(value: unknown, status = 200): Promise<Response> {
  return Promise.resolve(
    new Response(value === undefined ? null : JSON.stringify(value), {
      status,
      headers: { "Content-Type": "application/json" },
    }),
  );
}

/**
 * A reply that claims JSON and is not, so reading it throws inside the client.
 *
 * It reaches the error interceptor beside the Response it came from, the same way a refusal does,
 * which is the one case that tells a route that ran from a route that was declined.
 */
export function unparseableResponse(body: string, status: number): Promise<Response> {
  return Promise.resolve(
    new Response(body, { status, headers: { "Content-Type": "application/json" } }),
  );
}
