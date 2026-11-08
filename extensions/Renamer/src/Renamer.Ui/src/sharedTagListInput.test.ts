// @vitest-environment jsdom
// This test of a shared component lives in the consuming package because `react` resolves only here.
// The shared TagListInput driven as a combobox: what the box reads, which chips exist and what the
// list offers after each way of committing a value.
import { test, expect, vi, afterEach } from "vitest";
import { createElement, useState } from "react";
import { render, screen, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { TagListInput } from "@cove-extensions/ui-shared";

const SUGGESTIONS = ["title", "studio", "performers", "resolution"];

afterEach(cleanup);

function Host(props: {
  onLiveChange?: (raw: string) => void;
  onReject?: (candidate: string) => boolean;
}) {
  const [values, setValues] = useState<string[]>([]);
  return createElement(TagListInput, {
    values,
    onChange: setValues,
    suggestions: SUGGESTIONS,
    ariaLabel: "Tokens",
    placeholder: "Add a token",
    onLiveChange: props.onLiveChange,
    onReject: props.onReject,
  });
}

const box = () => screen.getByRole<HTMLInputElement>("combobox", { name: "Tokens" });
const offered = () => screen.queryAllByRole("option").map((o) => o.textContent.trim());
// The chips, read off the buttons that remove them, which is the only place a chip's value is
// exposed to a screen reader as well as on screen.
const chips = () =>
  screen
    .queryAllByRole("button", { name: /^Remove / })
    .map((b) => (b.getAttribute("aria-label") ?? "").slice("Remove ".length));

test("free text committed with Enter empties the box and offers the whole list again", async () => {
  const user = userEvent.setup();
  render(createElement(Host));

  await user.type(box(), "tit{Enter}");

  expect(chips()).toEqual(["tit"]);
  expect(box().value).toBe("");
  expect(offered()).toEqual(SUGGESTIONS);
});

test("Escape hides the list and typing on brings it back with no click away and back", async () => {
  const user = userEvent.setup();
  render(createElement(Host));

  await user.type(box(), "tit");
  expect(offered()).toEqual(["title"]);

  await user.keyboard("{Escape}");
  expect(offered()).toEqual([]);

  await user.keyboard("l");
  expect(offered()).toEqual(["title"]);
});

test("a value committed by leaving the field offers the whole list on the next visit", async () => {
  const user = userEvent.setup();
  render(createElement(Host));

  await user.type(box(), "tit");
  await user.tab();

  expect(chips()).toEqual(["tit"]);

  await user.click(box());
  expect(offered()).toEqual(SUGGESTIONS);
});

test("arrowing to a suggestion and pressing Enter adds that suggestion and empties the box", async () => {
  const user = userEvent.setup();
  render(createElement(Host));

  await user.type(box(), "stu{ArrowDown}{Enter}");

  expect(chips()).toEqual(["studio"]);
  expect(box().value).toBe("");
});

test("the sidecar advisory is told the box is empty once free text commits", async () => {
  const user = userEvent.setup();
  const onLiveChange = vi.fn<(raw: string) => void>();
  render(createElement(Host, { onLiveChange }));

  await user.type(box(), "tit{Enter}");

  expect(onLiveChange.mock.calls.at(-1)?.[0]).toBe("");
});

test("Enter on spaces alone adds nothing and leaves what was typed on screen", async () => {
  const user = userEvent.setup();
  render(createElement(Host));

  await user.type(box(), "   {Enter}");

  expect(chips()).toEqual([]);
  expect(box().value).toBe("   ");

  // The same Enter on a real token, which is what says the refusal above was the component's
  // choice rather than a key that never reached it.
  await user.clear(box());
  await user.type(box(), "title{Enter}");
  expect(chips()).toEqual(["title"]);
});

test("Enter on a rejected entry adds nothing and leaves the text there to correct", async () => {
  const user = userEvent.setup();
  render(createElement(Host, { onReject: (candidate) => candidate === "nope" }));

  await user.type(box(), "nope{Enter}");

  expect(chips()).toEqual([]);
  expect(box().value).toBe("nope");

  await user.clear(box());
  await user.type(box(), "fine{Enter}");
  expect(chips()).toEqual(["fine"]);
});

test("a token already picked is not accepted again in a different case", async () => {
  const user = userEvent.setup();
  render(createElement(Host));

  await user.type(box(), "studio{Enter}");
  expect(chips()).toEqual(["studio"]);

  // The rename engine resolves a token case-insensitively, so this is the token already on screen.
  await user.type(box(), "Studio{Enter}");

  expect(chips()).toEqual(["studio"]);
});

test("a token already picked is not offered again in a different case", async () => {
  const user = userEvent.setup();
  render(createElement(Host));

  await user.type(box(), "STUDIO{Enter}");

  expect(offered()).toEqual(["title", "performers", "resolution"]);
});

test("clicking a suggestion adds that option and not the half-typed query", async () => {
  const user = userEvent.setup();
  render(createElement(Host));

  await user.type(box(), "stu");
  expect(offered()).toEqual(["studio"]);

  await user.click(screen.getByRole("option", { name: "studio" }));

  // Exactly one chip, and the option's own spelling: "stu" alongside it would be the query
  // committed by a blur the click caused.
  expect(chips()).toEqual(["studio"]);
  expect(box().value).toBe("");
});
