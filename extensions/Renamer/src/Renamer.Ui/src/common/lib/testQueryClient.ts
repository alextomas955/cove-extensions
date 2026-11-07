/**
 * The QueryClient a test renders under, standing in for the one Cove mounts above every extension
 * surface.
 *
 * Retries are off: the host's own client retries a failed query once, which would make a test that
 * scripts one failed response wait for a second request and then assert against it. What each test
 * here is about is the response it scripted.
 *
 * A client per call, never a shared one: a cache carried between tests would serve the previous
 * test's data to the next one before its own request landed.
 */
import { createElement, type ReactElement, type ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

export function createTestQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

/**
 * Wraps a tree in a client, for a test that renders query hooks but never inspects the cache.
 *
 * A test that re-renders has to pass the same `client` each time. Letting this mint one per call
 * would give each render a cache of its own, and a walk already loaded would start again.
 */
export function withQueryClient(
  children: ReactNode,
  client: QueryClient = createTestQueryClient(),
): ReactElement {
  return createElement(QueryClientProvider, { client }, children);
}
