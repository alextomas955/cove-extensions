// @vitest-environment jsdom
// A DOM is needed because what is under test is whether the notice reaches the screen, which the
// pure module cannot show. The host's authenticated fetch and its shared primitives stand in,
// because each resolves only inside a consuming bundle.
import { expect, test, vi } from "vitest";
import { createElement, type ReactNode } from "react";

import { render } from "../common/lib/testRender";
import type { MissingPageView } from "../wire/api";

let answer: MissingPageView | null = null;

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
    Spinner: () => h("span", null, "reading"),
    StatusText: ({ children }: { children?: ReactNode }) => h("span", null, children),
  };
});

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: () => Promise.resolve(answer),
}));

vi.mock("@cove-extensions/ui-shared/postAction", () => ({
  postAction: () => new Promise(() => undefined),
}));

vi.mock("./hostComponents", () => ({
  ConfirmDialog: () => null,
  DetailListPagination: () => null,
  useKeySequence: () => undefined,
  useMultiSelect: () => ({
    selectedIds: new Set<string>(),
    toggle: () => undefined,
    selectIds: () => undefined,
    selectNone: () => undefined,
  }),
}));

const { WhisparrMissingTab } = await import("./MissingTab");
const copy = await import("../common/ui/copy");

function page(overrides: Partial<MissingPageView>): MissingPageView {
  return {
    cards: [],
    catalogueSize: 0,
    sizeIsLowerBound: false,
    instanceCatalogueCount: 0,
    providerCatalogueCount: null,
    page: 1,
    perPage: 40,
    lastPage: 1,
    rangeFrom: 0,
    rangeTo: 0,
    refusal: "none",
    facets: [],
    sorts: [],
    sortInForce: "DATE",
    providerName: "a source",
    ...overrides,
  };
}

async function tabOver(view: MissingPageView): Promise<HTMLElement> {
  answer = view;
  window.history.replaceState({}, "", "/studios/7");
  return render(createElement(WhisparrMissingTab, { entityId: 7 }));
}

// The selection bar keeps an empty live region for the refusal it may have to announce, so only
// the regions carrying words are sentences the reader is being told.
function sentences(tab: HTMLElement): string[] {
  return [...tab.querySelectorAll('[role="status"]')]
    .map((one) => one.textContent)
    .filter((one) => one !== "");
}

test("states both figures while Whisparr lists fewer than the source counts", async () => {
  const tab = await tabOver(
    page({
      instanceCatalogueCount: 340,
      providerCatalogueCount: 1205,
      catalogueSize: 12,
      rangeFrom: 1,
      rangeTo: 12,
    }),
  );

  expect(sentences(tab)).toContain(copy.catalogueStillArriving(340, 1205));
});

// The range above the grid is the other sentence the tab states, and it renders either way, so a
// tab that states it alone is one carrying no notice.
test("states nothing where the source answered no figure", async () => {
  const tab = await tabOver(
    page({
      instanceCatalogueCount: 340,
      providerCatalogueCount: null,
      catalogueSize: 12,
      rangeFrom: 1,
      rangeTo: 12,
    }),
  );

  expect(sentences(tab)).toEqual([copy.countLine(1, 12, 12, false)]);
});
