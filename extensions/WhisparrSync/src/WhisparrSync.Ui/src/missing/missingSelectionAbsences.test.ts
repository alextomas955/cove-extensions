// @vitest-environment jsdom
/**
 * Two things this surface deliberately does not do. Each is asserted, because an absence nobody
 * asserts is unfalsifiable and grows back in silence.
 *
 * The second one needs a real transport rather than a helper's return value: what is under test is
 * how many requests leave the browser between a press and the enqueue, and only a recorded transport
 * can count them.
 */
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";

import { press, render } from "../common/lib/testRender";
import { selectionActionsFor } from "./missingSelectionLogic";

interface Sent {
  path: string;
  method: string;
  body: string | undefined;
}

const sent: Sent[] = [];

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {},
  requestJson: (route: string) => {
    sent.push({ path: route, method: "GET", body: undefined });
    return Promise.resolve(PAGE_ANSWER);
  },
}));

vi.mock("@cove-extensions/ui-shared/postAction", () => ({
  postAction: (route: string, body: unknown) => {
    sent.push({ path: route, method: "POST", body: JSON.stringify(body) });
    return Promise.resolve({ jobId: "ext:missing-bulk:1", refusal: "none" });
  },
}));

vi.mock("@cove-extensions/ui-shared", () => ({
  extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
}));

const PAGE_ANSWER = {
  cards: [{ providerSceneId: "scene-a" }, { providerSceneId: "scene-b" }],
  page: 1,
  perPage: 40,
  lastPage: 1,
  refusal: "none",
};

const { useMissing } = await import("./useMissing");

afterEach(() => {
  sent.length = 0;
});

/**
 * Mounts the data layer behind a button that ticks every scene through the Select all gesture and
 * sends that selection, which is the whole path a reader takes.
 */
function Probe() {
  const missing = useMissing("studio", 7, { page: 1, sort: null, q: "", filters: {} });
  const loaded = (missing.state.view?.cards ?? []).map((card) => card.providerSceneId);
  return createElement("button", {
    type: "button",
    onClick: () => {
      const selectAll = selectionActionsFor(loaded, new Set());
      missing.monitorSelection(selectAll[0].resulting);
    },
  });
}

describe("no Select all matching control exists", () => {
  it("offers three gestures and none is named for matching beyond the page", () => {
    const actions = selectionActionsFor(["scene-a", "scene-b"], new Set());

    expect(actions).toHaveLength(3);
    for (const action of actions) {
      expect(
        action.label.toLowerCase(),
        `${action.key} is named for the whole result set`,
      ).not.toContain("matching");
    }
  });

  it("acts on nothing outside the loaded page", () => {
    const loaded = ["scene-a", "scene-b"];
    for (const action of selectionActionsFor(loaded, new Set(["scene-a"]))) {
      expect(action.resulting.every((id) => loaded.includes(id))).toBe(true);
    }
  });
});

describe("no server-side re-derivation runs before a bulk action", () => {
  it("enqueues the ids the loaded page carried, with no catalogue read in between", async () => {
    const container = await render(createElement(Probe));

    // The tab's own first read. Everything after the press is what this case counts.
    expect(sent.filter((request) => request.method === "GET")).toHaveLength(1);
    const before = sent.length;

    await press(container.querySelector("button") ?? undefined);

    const afterThePress = sent.slice(before);
    expect(afterThePress).toHaveLength(1);
    expect(afterThePress[0].method).toBe("POST");
    expect(afterThePress[0].path).toContain("/missing/bulk-monitor");
    // The scenes the one loaded page answered with, and no page was read to find them.
    expect(afterThePress[0].body).toBe(
      JSON.stringify({ providerSceneIds: ["scene-a", "scene-b"] }),
    );
  });
});
