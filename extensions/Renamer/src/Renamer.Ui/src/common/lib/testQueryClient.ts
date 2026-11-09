// Stands in for the QueryClient Cove mounts above every extension surface. Retries are off: the
// host's own client retries a failed query once, so a test that scripts one failed response would
// wait for a second request. A client per call, never a shared one, because a cache carried between
// tests would serve the previous test's data.
//
// The freshness window is the host's, copied from Cove's own createAppQueryClient. A test client
// that held none would let a query inherit this file's default instead of the host's, so a panel
// serving a cached answer where the real one re-reads would stay green here.
import { createElement, type ReactElement, type ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

export function createTestQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 30_000 } } });
}

// A test that re-renders has to pass the same `client` each time: the default mints one per call,
// so each render would get a cache of its own and a walk already loaded would start again.
export function withQueryClient(
  children: ReactNode,
  client: QueryClient = createTestQueryClient(),
): ReactElement {
  return createElement(QueryClientProvider, { client }, children);
}
