/**
 * Wait for a rendered condition, for a jsdom test driving a real component.
 *
 * A render commits on React's own schedule, so a test that sleeps a fixed span is betting the
 * machine finishes inside it. The bet is usually safe and silently wrong when it is not: a loaded
 * runner turns a passing assertion into a failing one with nothing to say why.
 *
 * Wait for the thing the next assertion is about - the element appearing, the text changing, the spy
 * having been called. A condition that already holds when the wait begins returns on the first poll
 * and proves nothing, so it is a fixed sleep of zero in a condition's clothes; pick the state the
 * action actually changes.
 *
 * On timeout the error names the condition, because "timed out" alone says only that something did
 * not happen.
 */
export async function waitFor(
  condition: string,
  holds: () => boolean,
  timeoutMs = 5_000,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    if (holds()) return;
    if (Date.now() > deadline) {
      throw new Error(`waited ${String(timeoutMs)}ms for ${condition}, which never became true`);
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}
