// @vitest-environment jsdom
/**
 * What the tab draws for one scene: a header, a fact row for each value the instance named, and one
 * full-width bar per control.
 *
 * A DOM is needed because the property under test is the SHAPE of what renders. A projection that
 * answers three nulls and a block that draws one row both pass a value-level check on either half.
 *
 * The host's authenticated fetch and its shared primitives stand in, because each resolves only inside a
 * consuming bundle.
 */
import { test, expect, vi } from "vitest";
import { createElement, type ReactNode } from "react";

import { render } from "../common/lib/testRender";
import type { SceneDetailView } from "../wire/api";

/** What each shared primitive this tab reaches for is handed. */
interface Slotted {
  icon?: ReactNode;
  children?: ReactNode;
}

/** What the button primitive is handed, which is what a control's own name is asserted through. */
interface Pressable extends Slotted {
  disabled?: boolean;
  onClick?: () => void;
  variant?: string;
  fill?: boolean;
}

let answer: Promise<SceneDetailView> = Promise.resolve(null as unknown as SceneDetailView);

vi.mock("@cove-extensions/ui-shared", () => ({
  extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
  // Stand-ins drawing their own slots and nothing else. What each primitive renders belongs to its
  // own suite; what this one asserts is which slot each value lands in.
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

/** The label of each fact row the block drew, in the order it drew them. */
const labels = (container: HTMLElement) =>
  [...container.querySelectorAll("dt")].map((label) => label.textContent);

/** The value each fact row drew, in the same order. */
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

  // The value truncates, so the whole of it has to stay reachable without it.
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

/** The name each control announces, in the order the tab drew them. */
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

  // All four go through the wrapper that takes a nullable reason, which is the only shape in which
  // a dimmed control with nothing to hear is unrepresentable.
  expect(container.querySelectorAll("span.flex.w-full > button")).toHaveLength(4);

  // Each fills its row, which is the property the shared button's own suite cannot check: the
  // primitive is mocked here.
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
