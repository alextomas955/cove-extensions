/**
 * The format examples this section shows beside each option.
 *
 * They are the only thing telling a user what a format string will produce, so a wrong one sends them
 * to a naming scheme they did not choose. Nothing in the panel computes them — they are hand-authored
 * strings — so nothing but a pin can catch one that is wrong.
 *
 * Every expectation below was produced by running the ENGINE's own formatter over the reference value
 * (`TimeSpan.ToString(format, InvariantCulture)`, as `MetadataProjector.FormatDuration` calls it) and
 * transcribed by hand. None is derived from the module under test, which would only prove it agrees
 * with itself. The whole list is pinned rather than each entry, so an option added with no example
 * checked here fails too.
 */
import { test, vi } from "vitest";
import assert from "node:assert/strict";

// The section reaches the host's entity selector transitively, and `@cove/runtime/*` resolves only
// inside a running Cove. Nothing here renders, so the stand-in only has to make the import resolve.
vi.mock("@cove/runtime/components", () => ({ EntityReferenceMultiSelector: () => null }));

const { DATE_FORMAT_OPTIONS, DURATION_FORMAT_OPTIONS } = await import("./TokenSettingsSection");

const pairs = (options: readonly { value: string; example: string }[]) =>
  options.map((o) => [o.value, o.example]);

test("every duration example is what the engine's formatter renders for 1h 23m 45s", () => {
  assert.deepEqual(pairs(DURATION_FORMAT_OPTIONS), [
    [String.raw`hh\-mm\-ss`, "01-23-45"],
    [String.raw`hh\.mm\.ss`, "01.23.45"],
    // `mm` is the minutes COMPONENT of 01:23:45, never its 83 total minutes.
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
