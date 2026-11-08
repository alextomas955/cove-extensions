// @vitest-environment jsdom
// The format examples beside each option, and the sentence ranking genders. Every expected example
// was produced by the engine's formatter (`TimeSpan.ToString(format, InvariantCulture)`, as
// `MetadataProjector.FormatDuration` calls it) and copied here, never derived from the module under
// test. The whole list is pinned, so an option added with no example checked here fails too.
import { test, expect, afterEach } from "vitest";
import assert from "node:assert/strict";
import { createElement } from "react";
import { render, screen, cleanup } from "@testing-library/react";

import { someOptions } from "./testOptions";

import {
  DATE_FORMAT_OPTIONS,
  DURATION_FORMAT_OPTIONS,
  TokenSettingsSection,
} from "./TokenSettingsSection";

const pairs = (options: readonly { value: string; example: string }[]) =>
  options.map((o) => [o.value, o.example]);

afterEach(cleanup);

test("every duration example is what the engine's formatter renders for 1h 23m 45s", () => {
  assert.deepEqual(pairs(DURATION_FORMAT_OPTIONS), [
    [String.raw`hh\-mm\-ss`, "01-23-45"],
    [String.raw`hh\.mm\.ss`, "01.23.45"],
    // `mm` is the minutes component of 01:23:45, never its 83 total minutes.
    [String.raw`mm\-ss`, "23-45"],
  ]);
});

test("every date example is what the engine's formatter renders for 2026-03-12", () => {
  assert.deepEqual(pairs(DATE_FORMAT_OPTIONS), [
    ["yyyy-MM-dd", "2026-03-12"],
    ["yyyy", "2026"],
    ["MM-dd-yyyy", "03-12-2026"],
    ["dd.MM.yyyy", "12.03.2026"],
    ["yyyy.MM.dd", "2026.03.12"],
  ]);
});

test("the gender order says where a gender the user left out ends up", () => {
  render(
    createElement(TokenSettingsSection, {
      // The performers group renders only for a template that uses the token.
      options: { ...someOptions(), filenameTemplate: "$performers - $title" },
      set: () => undefined,
      setMulti: () => undefined,
      insertToken: () => undefined,
    }),
  );

  expect(screen.queryAllByText("Most-preferred first. Anyone else sorts last.")).toHaveLength(1);
  expect(screen.queryAllByText("Most-preferred first.")).toHaveLength(0);
});
