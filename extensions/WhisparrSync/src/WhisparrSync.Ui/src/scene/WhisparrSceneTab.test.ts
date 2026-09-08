// @vitest-environment jsdom
/**
 * What the tab draws for one scene: the same four rows every time, with each absent value named in
 * the slot its value would have taken.
 *
 * A DOM is needed because the property under test is the SHAPE of what renders. A projection that
 * answers four nulls and a block that draws one row both pass a value-level check on either half.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is awaited on the condition it produces. The host's
 * authenticated fetch and its shared primitives stand in, because each resolves only inside a
 * consuming bundle.
 */
import { test, expect, vi, afterEach } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import type { SceneDetailView } from "../wire/api";

/** What each shared primitive this tab reaches for is handed. */
interface Slotted {
  icon?: ReactNode;
  children?: ReactNode;
}

let answer: Promise<SceneDetailView> = Promise.resolve(null as unknown as SceneDetailView);

vi.mock("@cove-extensions/ui-shared", () => ({
  extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
  // Stand-ins drawing their own slots and nothing else. What each primitive renders belongs to its
  // own suite; what this one asserts is which slot each value lands in.
  StatusPill: ({ icon, children }: Slotted) => createElement("span", null, icon, children),
  StatusText: ({ children }: Slotted) => createElement("span", null, children),
  Spinner: () => createElement("span", null, "reading"),
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: () => answer,
}));

const { WhisparrSceneTab } = await import("./WhisparrSceneTab");
const copy = await import("../common/ui/copy");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

const teardowns: (() => void)[] = [];
afterEach(() => {
  while (teardowns.length > 0) teardowns.pop()?.();
});

function view(overrides: Partial<SceneDetailView> = {}): SceneDetailView {
  return {
    refusal: "none",
    excluded: false,
    present: true,
    monitored: true,
    qualityName: null,
    qualityProfileName: null,
    cutoffName: null,
    profileReadDidNotComplete: false,
    ...overrides,
  };
}

async function mount(answered: SceneDetailView): Promise<HTMLElement> {
  answer = Promise.resolve(answered);
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(WhisparrSceneTab, { entityId: 1 }));
  await sleep(COMMIT_MS);

  teardowns.push(() => {
    root.unmount();
    container.remove();
  });
  return container;
}

/** The label of each fact row the block drew, in the order it drew them. */
const labels = (container: HTMLElement) =>
  [...container.querySelectorAll("dt")].map((label) => label.textContent);

/** The value each fact row drew, in the same order. */
const values = (container: HTMLElement) =>
  [...container.querySelectorAll("dd")].map((value) => value.textContent);

test("a scene with nothing named still draws all four rows, each absence stated", async () => {
  const container = await mount(view());

  expect(labels(container)).toEqual([
    copy.SCENE_FACT_STATE,
    copy.SCENE_FACT_QUALITY,
    copy.SCENE_FACT_PROFILE,
    copy.SCENE_FACT_CUTOFF,
  ]);
  expect(values(container)).toHaveLength(4);
  expect(values(container)[1]).toBe(copy.SCENE_HAS_NO_FILE_YET);
  expect(values(container)[2]).toBe(copy.SCENE_IS_NOT_IN_WHISPARR);
  expect(values(container)[3]).toBe(copy.SCENE_CUTOFF_NOT_NAMED);
});

test("a scene the instance does not hold names its own absence in both profile rows", async () => {
  const container = await mount(view({ present: false, monitored: null }));

  expect(labels(container)).toHaveLength(4);
  expect(values(container)[2]).toBe(copy.SCENE_IS_NOT_IN_WHISPARR);
  expect(values(container)[3]).toBe(copy.SCENE_IS_NOT_IN_WHISPARR);
});

test("the instance's own names are drawn verbatim, with the full text reachable", async () => {
  const container = await mount(
    view({
      qualityName: "WEBDL-1080p",
      qualityProfileName: "Any but the very worst thing an indexer ever listed",
      cutoffName: "WEBDL-1080p",
    }),
  );

  expect(values(container)[1]).toBe("WEBDL-1080p");
  expect(values(container)[2]).toBe("Any but the very worst thing an indexer ever listed");
  expect(values(container)[3]).toBe("WEBDL-1080p");

  // The value truncates, so the whole of it has to stay reachable without it.
  const wide = [...container.querySelectorAll("dd")][2];
  expect(wide.className).toContain("truncate");
  expect(wide.getAttribute("title")).toBe("Any but the very worst thing an indexer ever listed");
});

test("a profile read that established nothing keeps the scene facts and says so", async () => {
  const container = await mount(
    view({ qualityName: "WEBDL-1080p", profileReadDidNotComplete: true }),
  );

  expect(container.textContent).toContain(copy.THE_STATUS_READ_DID_NOT_COMPLETE);
  expect(labels(container)).toHaveLength(4);
  expect(values(container)[1]).toBe("WEBDL-1080p");
});

test("a refused answer states its reason and draws no fact block", async () => {
  const container = await mount(
    view({ refusal: "noIdentityInThisNamespace", present: null, monitored: null }),
  );

  expect(container.textContent).toContain(copy.NO_IDENTITY_IN_THIS_NAMESPACE);
  expect(labels(container)).toEqual([]);
});
