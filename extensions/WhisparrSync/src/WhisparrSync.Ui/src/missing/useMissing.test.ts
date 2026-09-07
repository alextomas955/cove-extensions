// @vitest-environment jsdom
/**
 * Which address the tab's bulk path asks for, and what it carries there.
 *
 * A DOM is needed because the property under test is what a mounted hook sends, not what a helper
 * returns: a route composed correctly and never reached, or reached with a body naming other scenes,
 * both pass a value-level check on the composer alone.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is awaited on the condition it produces. The host's
 * authenticated fetch and its POST helper stand in, because each resolves only inside a consuming
 * bundle.
 */
import { test, expect, vi, afterEach } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

vi.mock("@cove-extensions/ui-shared", () => ({
  // The real builder, because the address the browser asks for is what is under test.
  extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
}));

interface Sent {
  path: string;
  method: string;
  body: string | undefined;
}

const sent: Sent[] = [];

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: (route: string) => {
    sent.push({ path: route, method: "GET", body: undefined });
    return Promise.resolve(null);
  },
}));

vi.mock("@cove-extensions/ui-shared/postAction", () => ({
  postAction: (route: string, body: unknown) => {
    sent.push({ path: route, method: "POST", body: JSON.stringify(body) });
    return Promise.resolve({ jobId: "job-1", refusal: "none" });
  },
}));

const { useMissing } = await import("./useMissing");
const { DEFAULT_MISSING_VIEW } = await import("./missingUrlLogic");

const FIRST_SCENE = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
const SECOND_SCENE = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Polls `until` until it holds, so a render is waited for rather than a number of milliseconds. */
async function settled(until: () => boolean, budgetMs = 2000): Promise<boolean> {
  const deadline = Date.now() + budgetMs;
  while (!until() && Date.now() < deadline) {
    await sleep(5);
  }
  return until();
}

const teardowns: (() => void)[] = [];
afterEach(() => {
  while (teardowns.length > 0) teardowns.pop()?.();
  sent.length = 0;
});

/** Mounts the hook over one studio and hands back what it returns. */
async function mount(coveId: number) {
  let latest: ReturnType<typeof useMissing> | null = null;
  function Probe() {
    latest = useMissing("studio", coveId, DEFAULT_MISSING_VIEW);
    return null;
  }

  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(Probe));
  const mounted = await settled(() => latest !== null);
  teardowns.push(() => {
    root.unmount();
    container.remove();
  });

  expect(mounted, "the hook never committed").toBe(true);
  return latest as unknown as ReturnType<typeof useMissing>;
}

const posted = () => sent.filter((call) => call.method === "POST");

test("the selection asks for the bulk route and carries exactly the ticked scenes", async () => {
  const missing = await mount(42);

  missing.monitorSelection([FIRST_SCENE, SECOND_SCENE]);
  expect(await settled(() => posted().length === 1)).toBe(true);

  expect(posted()).toHaveLength(1);
  expect(posted()[0].path.endsWith("/entity/studio/42/missing/bulk-monitor")).toBe(true);
  expect(JSON.parse(posted()[0].body ?? "{}")).toEqual({
    providerSceneIds: [FIRST_SCENE, SECOND_SCENE],
  });
});

/**
 * The entity route registers the scenes the library holds, and every scene on this tab is one it
 * does not, so a call from here would act on the opposite set.
 */
test("nothing the tab sends reaches the whole-entity route", async () => {
  const missing = await mount(42);

  missing.monitorSelection([FIRST_SCENE, SECOND_SCENE]);
  missing.monitorScene(FIRST_SCENE);
  missing.searchScene(FIRST_SCENE);
  missing.refresh();
  expect(await settled(() => posted().length === 3)).toBe(true);

  expect(sent.length).toBeGreaterThan(0);
  expect(sent.filter((call) => call.path.endsWith("add-all-missing"))).toEqual([]);
});
