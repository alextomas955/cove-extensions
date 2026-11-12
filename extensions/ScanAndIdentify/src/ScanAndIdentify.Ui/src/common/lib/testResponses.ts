// The replies a fake `extensionFetch` hands back. The generated client reads a reply by its
// Content-Type, so a fake that answers with a bare Response and no header takes a different path
// through the client than the server does.

/** A reply the client can parse. `undefined` sends no body, which is what a 204 answers. */
export function jsonResponse(value: unknown, status = 200): Promise<Response> {
  return Promise.resolve(
    new Response(value === undefined ? null : JSON.stringify(value), {
      status,
      headers: { "Content-Type": "application/json" },
    }),
  );
}
