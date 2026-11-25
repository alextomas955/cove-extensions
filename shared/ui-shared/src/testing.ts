// Test-only helpers every UI suite needs, kept out of the barrel so no bundle can reach them.
//
// A suite drives the real component against the real generated client, so what it has to supply is
// the two things the host supplies in production: the QueryClient mounted above every extension
// surface, and the replies a fetch hands back.
import { createElement, type ReactElement, type ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

// Retries are off: the host's own client retries a failed query once, so a test that scripts one
// failed response would wait for a second request. A client per call, never a shared one, because a
// cache carried between tests would serve the previous test's data.
//
// The freshness window is the host's, copied from Cove's own createAppQueryClient. A test client
// that held none would let a query inherit this file's default instead of the host's, so a panel
// serving a cached answer where the real one re-reads would stay green here.
export function createTestQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 30_000 } } });
}

/**
 * Mounts `children` under a QueryClient, standing in for the one Cove mounts above every extension
 * surface.
 *
 * A test that re-renders has to pass the same `client` each time: the default mints one per call,
 * so each render would get a cache of its own and a walk already loaded would start again.
 */
export function withQueryClient(
  children: ReactNode,
  client: QueryClient = createTestQueryClient(),
): ReactElement {
  return createElement(QueryClientProvider, { client }, children);
}

/**
 * A reply the generated client can parse. `undefined` sends no body, which is what a 204 answers.
 *
 * The client reads a reply by its Content-Type, so a fake that answers with a bare Response and no
 * header takes a different path through the client than the server does.
 */
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
 *
 * Which of the two a case wants is stated at the call site rather than inferred from the body's
 * type: a string body is a reply that parses to a string under {@link jsonResponse} and a reply
 * that does not parse at all under this one.
 */
export function unparseableResponse(body: string, status: number): Promise<Response> {
  return Promise.resolve(
    new Response(body, { status, headers: { "Content-Type": "application/json" } }),
  );
}
