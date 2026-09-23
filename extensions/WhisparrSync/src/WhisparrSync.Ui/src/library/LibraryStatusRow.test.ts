// @vitest-environment jsdom
// The row is rendered beside the card badges it counts. Feeding the row its own numbers would let
// it agree with itself while the two surfaces disagreed on screen.
//
// The shared primitives and the host's authenticated request are mocked because each resolves only
// inside a consuming bundle.
import { afterEach, expect, test, vi } from "vitest";
import { createElement, type ReactNode } from "react";

import { render } from "../common/lib/testRender";
import { FILE_MARKER } from "../common/ui/stateVocabularyLogic";
import type { LibraryCardReading, LibraryStatusView } from "../wire/api";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
    Spinner: () => h("span", null, "checking"),
    StatusPill: (props: { children: ReactNode; icon?: ReactNode }) =>
      h("span", null, props.children),
  };
});

const requestJson = vi.fn<(path: string, options: unknown) => Promise<LibraryStatusView>>();

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: (path: string, options: unknown): Promise<LibraryStatusView> =>
    requestJson(path, options),
}));

const { WhisparrVideoLibraryRow, WhisparrStudioLibraryRow } = await import("./LibraryStatusRow");
const { WhisparrVideoCardBadge } = await import("./WhisparrVideoCardBadge");
const { WhisparrStudioCardBadge } = await import("./WhisparrEntityCardBadge");
const { libraryStatusOn, toggleLibraryStatus } = await import("./libraryToggleStore");

afterEach(() => {
  if (libraryStatusOn()) toggleLibraryStatus();
  requestJson.mockReset();
});

function showBadges(): void {
  if (!libraryStatusOn()) toggleLibraryStatus();
}

function answering(readings: (LibraryCardReading | null)[]): void {
  requestJson.mockImplementation((_path, options) => {
    const body = JSON.parse((options as { body: string }).body) as { coveIds: number[] };
    return Promise.resolve({
      kind: "video",
      rows: body.coveIds.map((coveId, index) => ({ coveId, reading: readings[index] ?? null })),
      refusal: "none",
      moreNotAnswered: false,
    });
  });
}

// The row and the cards are mounted together, as the host mounts them.
async function pageOf(readings: (LibraryCardReading | null)[]): Promise<HTMLDivElement> {
  answering(readings);
  return render(
    createElement(
      "div",
      null,
      createElement(WhisparrVideoLibraryRow),
      ...readings.map((_reading, index) =>
        createElement(WhisparrVideoCardBadge, { key: index, video: { id: index + 1 } }),
      ),
    ),
  );
}

test("the row counts the cards on the page and names what it counted", async () => {
  showBadges();

  const page = await pageOf([
    { excluded: false, present: true, monitored: true },
    { excluded: false, present: true, monitored: true },
    { excluded: false, present: true, monitored: false },
    { excluded: false, present: false, monitored: null },
  ]);

  const row = page.querySelector("[role=status]");
  expect(row?.textContent).toContain("2Monitored");
  expect(row?.textContent).toContain("1Unmonitored");
  expect(row?.textContent).toContain("1not added on this page");
});

test("the row says its counts are the page's and not the library's", async () => {
  showBadges();

  const page = await pageOf([{ excluded: false, present: true, monitored: true }]);

  expect(page.querySelector("[role=status]")?.getAttribute("title")).toBe(
    "These counts are for the cards on this page, not for the whole library.",
  );
});

test("the row asks for nothing of its own", async () => {
  showBadges();

  await pageOf([{ excluded: false, present: true, monitored: true }]);

  // One request for the whole page, which is the badges' own. A second would be the row asking.
  expect(requestJson).toHaveBeenCalledTimes(1);
});

test("the row is absent until a reader asks for the status", async () => {
  const page = await pageOf([{ excluded: false, present: true, monitored: true }]);

  expect(page.querySelector("[role=status]")).toBeNull();
  expect(requestJson).not.toHaveBeenCalled();
});

test("a page the extension cannot speak for draws no row of zeroes", async () => {
  showBadges();

  const page = await pageOf([null, null]);

  expect(page.querySelector("[role=status]")).toBeNull();
});

test("cards the instance answered nothing usable for are counted as unknown, not as absent", async () => {
  showBadges();

  const page = await pageOf([
    { excluded: false, present: null, monitored: null },
    { excluded: false, present: null, monitored: null },
  ]);

  const row = page.querySelector("[role=status]");
  expect(row?.textContent).toContain("2Status unknown");
  expect(row?.textContent).toContain("0not added on this page");
});

test("a file is counted beside the states it cross-cuts", async () => {
  showBadges();

  const page = await pageOf([
    { excluded: false, present: true, monitored: true, inLibrary: true },
    { excluded: false, present: true, monitored: false, inLibrary: true },
  ]);

  const row = page.querySelector("[role=status]");
  expect(row?.textContent).toContain("2In library");
  expect(row?.textContent).toContain("1Monitored");
  expect(row?.textContent).toContain("1Unmonitored");
});

test("the key keeps every entry at a count of zero, so it does not change as you page", async () => {
  showBadges();

  const page = await pageOf([{ excluded: false, present: false, monitored: null }]);

  const row = page.querySelector("[role=status]");
  expect(row?.textContent).toContain("0Monitored");
  expect(row?.textContent).toContain("0Unmonitored");
  expect(row?.textContent).toContain("0Excluded");
  expect(row?.textContent).toContain("0In library");
});

test("the unknown state is drawn only where a card is in it", async () => {
  showBadges();

  const known = await pageOf([{ excluded: false, present: true, monitored: true }]);
  expect(known.querySelector("[role=status]")?.textContent).not.toContain("Status unknown");
});

test("a display mode that mounts no card mounts no row either", async () => {
  showBadges();

  const page = await render(createElement(WhisparrVideoLibraryRow));

  expect(page.querySelector("[role=status]")).toBeNull();
});

// Holding a file is a fact about one scene. No answer on the studio path carries one, so a figure
// there would read as none held when nothing was ever asked.
test("the studio row draws no file figure, because no studio answer carries one", async () => {
  requestJson.mockImplementation((_path, options) => {
    const body = JSON.parse((options as { body: string }).body) as { coveIds: number[] };
    return Promise.resolve({
      kind: "studio",
      rows: body.coveIds.map((coveId) => ({
        coveId,
        reading: { excluded: false, present: true, monitored: false, inLibrary: null },
      })),
      refusal: "none",
      moreNotAnswered: false,
    });
  });
  showBadges();

  const page = await render(
    createElement(
      "div",
      null,
      createElement(WhisparrStudioLibraryRow),
      createElement(WhisparrStudioCardBadge, { studio: { id: 1 } }),
    ),
  );

  expect(page.textContent).toContain("Unmonitored");
  expect(page.textContent).not.toContain(FILE_MARKER.label);
});
