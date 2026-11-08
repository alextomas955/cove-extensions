// Stands in for the QueryClient Cove mounts above every extension surface. Retries are off: the
// host's own client retries a failed query once, so a test that scripts one failed response would
// wait for a second request. A client per call, never a shared one, because a cache carried between
// tests would serve the previous test's data.
import { createElement, type ReactElement, type ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

export function createTestQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

// A test that re-renders has to pass the same `client` each time: the default mints one per call,
// so each render would get a cache of its own and a walk already loaded would start again.
export function withQueryClient(
  children: ReactNode,
  client: QueryClient = createTestQueryClient(),
): ReactElement {
  return createElement(QueryClientProvider, { client }, children);
}
