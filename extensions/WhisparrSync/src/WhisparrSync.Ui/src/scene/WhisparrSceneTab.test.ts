// @vitest-environment jsdom
// A DOM is needed because what is under test is the shape of what renders, not a value. The
// host's authenticated fetch and its shared primitives stand in, because each resolves only
// inside a consuming bundle.
import { test, expect, vi } from "vitest";
import { createElement, type ReactNode } from "react";

import { render } from "../common/lib/testRender";
import type { SceneDetailView } from "../wire/api";

interface Slotted {
  icon?: ReactNode;
  children?: ReactNode;
}

interface Pressable extends Slotted {
  disabled?: boolean;
  onClick?: () => void;
  variant?: string;
  fill?: boolean;
}

let answer: Promise<SceneDetailView> = Promise.resolve(null as unknown as SceneDetailView);

vi.mock("@cove-extensions/ui-shared", () => ({
  extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
  // Stand-ins drawing their own slots and nothing else, so what is asserted below is which slot
  // each value lands in.
  StatusPill: ({ icon, children }: Slotted) => createElement("span", null, icon, children),
  StatusText: ({ children }: Slotted) => createElement("span", null, children),
  Spinner: () => createElement("span", null, "reading"),
  Button: ({ children, disabled, onClick, variant, fill }: Pressable) =>
    createElement(
      "button",
      { disabled, onClick, "data-variant": variant, "data-fill": fill === true ? "" : undefined },
      children,
    ),
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: () => answer,
}));

vi.mock("@cove-extensions/ui-shared/postAction", () => ({
  postAction: () => new Promise(() => undefined),
}));

const { WhisparrSceneTab } = await import("./WhisparrSceneTab");
const copy = await import("../common/ui/copy");

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
  return render(createElement(WhisparrSceneTab, { entityId: 1 }));
}

const labels = (container: HTMLElement) =>
  [...container.querySelectorAll("dt")].map((label) => label.textContent);

const values = (container: HTMLElement) =>
  [...container.querySelectorAll("dd")].map((value) => value.textContent);

test("the header names the product beside the state chip", async () => {
  const container = await mount(view());

  expect(container.textContent).toContain(copy.SCENE_HEADER_WHISPARR);
});

test("a scene the instance named nothing for draws no fact rows at all", async () => {
  const container = await mount(view());

  expect(labels(container)).toEqual([]);
  expect(values(container)).toEqual([]);
});

test("a scene the instance does not hold draws no fact rows and still states its state", async () => {
  const container = await mount(view({ present: false, monitored: null }));

  expect(labels(container)).toEqual([]);
  expect(container.textContent).toContain(copy.SCENE_HEADER_WHISPARR);
});

test("the instance's own names are drawn verbatim, with the full text reachable", async () => {
  const container = await mount(
    view({
      qualityName: "WEBDL-1080p",
      qualityProfileName: "Any but the very worst thing an indexer ever listed",
      cutoffName: "WEBDL-1080p",
    }),
  );

  expect(labels(container)).toEqual([
    copy.SCENE_FACT_QUALITY,
    copy.SCENE_FACT_PROFILE,
    copy.SCENE_FACT_CUTOFF,
  ]);
  expect(values(container)[0]).toBe("WEBDL-1080p");
  expect(values(container)[1]).toBe("Any but the very worst thing an indexer ever listed");
  expect(values(container)[2]).toBe("WEBDL-1080p");

  // The value truncates, so the whole of it stays reachable in the title.
  const wide = [...container.querySelectorAll("dd")][1];
  expect(wide.className).toContain("truncate");
  expect(wide.getAttribute("title")).toBe("Any but the very worst thing an indexer ever listed");
});

test("a profile read that established nothing keeps the named fact and says so", async () => {
  const container = await mount(
    view({ qualityName: "WEBDL-1080p", profileReadDidNotComplete: true }),
  );

  expect(container.textContent).toContain(copy.THE_STATUS_READ_DID_NOT_COMPLETE);
  expect(labels(container)).toEqual([copy.SCENE_FACT_QUALITY]);
  expect(values(container)[0]).toBe("WEBDL-1080p");
});

const controlNames = (container: HTMLElement) =>
  [...container.querySelectorAll("button")].map((control) => control.textContent);

test("the four controls draw as bars, each announcing its own name", async () => {
  const container = await mount(view({ present: false, monitored: null }));

  // A disabled control announces its own name and then its reason, so the two it stops carry both.
  expect(controlNames(container)).toEqual([
    copy.SCENE_ADD,
    copy.MONITOR_IN_WHISPARR + copy.SCENE_MONITOR_NEEDS_AN_ENTRY,
    copy.SCENE_SEARCH + copy.SCENE_SEARCH_NEEDS_AN_ENTRY,
    copy.SCENE_EXCLUDE,
  ]);

  // All four go through the wrapper that takes a nullable reason, so a disabled control with no
  // stated reason is unrepresentable.
  expect(container.querySelectorAll("span.flex.w-full > button")).toHaveLength(4);

  // Each fills its row.
  expect(container.querySelectorAll("button[data-fill]")).toHaveLength(4);

  expect(container.querySelectorAll("p")).toHaveLength(0);
});

test("a disabled control announces its own name and then its reason", async () => {
  const container = await mount(view({ present: false, monitored: null }));
  const monitor = [...container.querySelectorAll("button")][1];

  expect(monitor.disabled).toBe(true);
  expect(monitor.textContent).toBe(copy.MONITOR_IN_WHISPARR + copy.SCENE_MONITOR_NEEDS_AN_ENTRY);
  expect(monitor.closest("span")?.getAttribute("title")).toBe(copy.SCENE_MONITOR_NEEDS_AN_ENTRY);
});

test("a refused answer states its reason and draws no fact block", async () => {
  const container = await mount(
    view({ refusal: "noIdentityInThisNamespace", present: null, monitored: null }),
  );

  expect(container.textContent).toContain(copy.NO_IDENTITY_IN_THIS_NAMESPACE);
  expect(labels(container)).toEqual([]);
});
