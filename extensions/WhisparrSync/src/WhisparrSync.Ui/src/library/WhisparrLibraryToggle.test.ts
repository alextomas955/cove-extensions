// @vitest-environment jsdom
// The control carries no visible label, so its `aria-label` and hover text are the only name it
// has.
//
// The host's route builder and its authenticated request are mocked because each resolves only
// inside a consuming bundle.
import { act, createElement, type ReactNode } from "react";
import { afterEach, expect, test, vi } from "vitest";

import { render as renderRoot } from "../common/lib/testRender";
import type { LibraryStatusView } from "../wire/api";

vi.mock("@cove-extensions/ui-shared", () => ({
  // The real builder, because the store under this control composes the route it asks for.
  extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
}));

const requestJson = vi.fn<(path: string, options: unknown) => Promise<LibraryStatusView>>();

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: (path: string, options: unknown): Promise<LibraryStatusView> =>
    requestJson(path, options),
}));

const { WhisparrLibraryToggle } = await import("./WhisparrLibraryToggle");
const { requestCardStatus } = await import("./cardStatusStore");
const { libraryStatusOn, toggleLibraryStatus } = await import("./libraryToggleStore");
const { HIDE_WHISPARR_STATUS, SHOW_WHISPARR_STATUS, WHISPARR_STATUS_COULD_NOT_BE_READ } =
  await import("../common/ui/copy");

const teardowns: (() => void)[] = [];

afterEach(() => {
  // The card releases run before the boolean is restored, so the next test's first request begins
  // with no answer held and drops the reason this one established.
  while (teardowns.length > 0) teardowns.pop()?.();
  if (libraryStatusOn()) toggleLibraryStatus();
  requestJson.mockReset();
});

async function aBlankStudioCardOnScreen(): Promise<void> {
  requestJson.mockResolvedValue({
    kind: "studio",
    rows: [{ coveId: 1, reading: null }],
    refusal: "instanceUnreachable",
    moreNotAnswered: false,
  });
  await act(() => {
    teardowns.push(requestCardStatus("studio", 1));
    return Promise.resolve();
  });
}

async function render(node: ReactNode): Promise<() => HTMLButtonElement | null> {
  const container = await renderRoot(node);
  return () => container.querySelector("button");
}

async function flip(): Promise<void> {
  await act(() => {
    toggleLibraryStatus();
    return Promise.resolve();
  });
}

test("an on control states the page's reason while a blank card of that kind is on screen", async () => {
  await aBlankStudioCardOnScreen();
  toggleLibraryStatus();

  const button = await render(createElement(WhisparrLibraryToggle));
  const spoken = `${HIDE_WHISPARR_STATUS}. ${WHISPARR_STATUS_COULD_NOT_BE_READ}`;

  expect(button()?.getAttribute("aria-pressed")).toBe("true");
  expect(
    button()?.getAttribute("aria-label"),
    "a card that drew no badge left the page with no reason on the control",
  ).toBe(spoken);
  expect(button()?.getAttribute("title")).toBe(spoken);
});

test("an off control states no reason, however the read before it ended", async () => {
  await aBlankStudioCardOnScreen();
  toggleLibraryStatus();
  const button = await render(createElement(WhisparrLibraryToggle));

  // Asserted before the press, so the case cannot pass on a control that never held a reason.
  expect(button()?.getAttribute("aria-label")).toContain(WHISPARR_STATUS_COULD_NOT_BE_READ);

  await flip();

  expect(button()?.getAttribute("aria-pressed")).toBe("false");
  expect(
    button()?.getAttribute("aria-label"),
    "a control claiming nothing about any card still stated a reason for one",
  ).toBe(SHOW_WHISPARR_STATUS);
  expect(button()?.getAttribute("title")).toBe(SHOW_WHISPARR_STATUS);
});

test("the reason comes back with a second press, while the blank card is still on screen", async () => {
  await aBlankStudioCardOnScreen();
  toggleLibraryStatus();
  const button = await render(createElement(WhisparrLibraryToggle));

  await flip();
  await flip();

  expect(
    button()?.getAttribute("aria-label"),
    "pressing the control off and on again lost the reason its own page established",
  ).toBe(`${HIDE_WHISPARR_STATUS}. ${WHISPARR_STATUS_COULD_NOT_BE_READ}`);
});
