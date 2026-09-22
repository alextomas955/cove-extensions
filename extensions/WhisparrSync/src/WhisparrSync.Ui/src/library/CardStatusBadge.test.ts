// @vitest-environment jsdom
// The shared primitives and the host's authenticated request are mocked because each resolves only
// inside a consuming bundle.
import { afterEach, expect, test, vi } from "vitest";
import { createElement, type ReactNode } from "react";

import { render } from "../common/lib/testRender";
import type { LibraryStatusView } from "../wire/api";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    // The real builder, because the address the browser asks for is under test.
    extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
    Spinner: () => h("span", null, "…"),
    StatusPill: (props: { children: ReactNode; icon?: ReactNode }) =>
      h("span", null, props.icon, props.children),
  };
});

const requestJson = vi.fn<(path: string, options: unknown) => Promise<LibraryStatusView>>();

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: (path: string, options: unknown): Promise<LibraryStatusView> =>
    requestJson(path, options),
}));

const { WhisparrPerformerCardBadge, WhisparrStudioCardBadge } =
  await import("./WhisparrEntityCardBadge");
const { WhisparrVideoCardBadge } = await import("./WhisparrVideoCardBadge");
const { libraryStatusOn, toggleLibraryStatus } = await import("./libraryToggleStore");

// A host object as the slot delivers one. The identity rows and title beside the Cove id are what
// the browser must not send anywhere.
const HOST_OBJECT = {
  id: 7,
  name: "A studio nobody outside this library knows about",
  externalId: "an-identity-row-value",
  providerIds: ["another-identity-row-value"],
};

function asked(call = 0): { path: string; body: Record<string, unknown> } {
  const [path, options] = requestJson.mock.calls[call] as unknown as [string, { body: string }];
  return { path, body: JSON.parse(options.body) as Record<string, unknown> };
}

afterEach(() => {
  if (libraryStatusOn()) toggleLibraryStatus();
  requestJson.mockReset();
});

function answering(rows: LibraryStatusView["rows"]): void {
  requestJson.mockResolvedValue({ kind: "studio", rows, refusal: "none", moreNotAnswered: false });
}

function showBadges(): void {
  if (!libraryStatusOn()) toggleLibraryStatus();
}

test("a badge asks for its own card kind, naming the Cove id and nothing else", async () => {
  answering([]);
  showBadges();

  await render(createElement(WhisparrStudioCardBadge, { studio: HOST_OBJECT }));

  expect(requestJson).toHaveBeenCalledTimes(1);
  expect(asked().path).toBe("/extensions/com.alextomas955.whisparrsync/library/studio/status");
  // The whole body, so a member nobody thought to forbid fails here too.
  expect(asked().body).toEqual({ coveIds: [7] });
});

test("nothing else the host object carries reaches the request at all", async () => {
  answering([]);
  showBadges();

  await render(createElement(WhisparrVideoCardBadge, { video: HOST_OBJECT }));

  const sent = JSON.stringify(requestJson.mock.calls[0]);
  for (const value of [HOST_OBJECT.name, HOST_OBJECT.externalId, ...HOST_OBJECT.providerIds]) {
    expect(sent, `the request carried ${value}`).not.toContain(value);
  }
});

test("each card kind rides in the address rather than in the body", async () => {
  answering([]);
  showBadges();

  await render(createElement(WhisparrPerformerCardBadge, { performer: HOST_OBJECT }));

  expect(asked().path).toBe("/extensions/com.alextomas955.whisparrsync/library/performer/status");
  expect(asked().body).toEqual({ coveIds: [7] });
});

test("a badge sends nothing at all until a reader asks for the badges", async () => {
  answering([]);

  const container = await render(createElement(WhisparrStudioCardBadge, { studio: HOST_OBJECT }));

  expect(requestJson).not.toHaveBeenCalled();
  expect(container.textContent).toBe("");
});

test("the answered reading is drawn on the card, and a row with none says it is not linked", async () => {
  answering([{ coveId: 7, reading: { excluded: false, present: true, monitored: true } }]);
  showBadges();

  const drawn = await render(createElement(WhisparrStudioCardBadge, { studio: HOST_OBJECT }));
  expect(drawn.textContent).not.toBe("");

  // The library holds no id the connected generation could name the studio by, so the instance was
  // never asked. Left blank, the card could not be told from one whose read is still in flight.
  answering([{ coveId: 8, reading: null }]);
  const unlinked = await render(
    createElement(WhisparrStudioCardBadge, { studio: { ...HOST_OBJECT, id: 8 } }),
  );
  expect(unlinked.textContent).toContain("Not linked");
});
