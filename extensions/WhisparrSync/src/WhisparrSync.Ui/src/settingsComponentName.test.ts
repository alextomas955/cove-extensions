/**
 * The strings that span both tiers: every `componentName` the C# manifest advertises, and every key
 * this bundle registers a component under. The host resolves one to the other by exact string, and
 * renders nothing with no error anywhere when they differ, so neither side can detect a drift alone.
 *
 * Each name is read from the tier that owns it, never from a literal here: a value copied into this
 * file would agree with whichever side it was copied from and stop reporting the other.
 *
 * Both sets are compared, not a single pair. A manifest that advertises a name the bundle does not
 * register renders an empty slot, and a bundle key nothing advertises is code the host never asks
 * for; the two failures read the same from either side alone.
 */
import { readFileSync } from "node:fs";
import path from "node:path";
import { test, expect } from "vitest";

const bundleEntry = path.join(import.meta.dirname, "index.ts");
const manifestSource = path.resolve(
  import.meta.dirname,
  "..",
  "..",
  "WhisparrSync",
  "WhisparrSync.Api.cs",
);

/** The whole body of `defineExtension({ components: { … } })` in the bundle entry. */
const COMPONENT_MAP = /components:\s*\{([^}]*)\}/;

/** Each key of that map, whether it is shorthand or `Key: Value`. */
const COMPONENT_MAP_KEY = /([A-Za-z_$][\w$]*)\s*(?::\s*[A-Za-z_$][\w$]*)?\s*(?:,|$)/g;

/** Every `componentName:` argument in the manifest override. */
const MANIFEST_COMPONENT_NAME = /componentName:\s*"([^"]*)"/g;

/** Every capture of `pattern` in `file`, with the match count asserted to be at least one. */
function readAll(pattern: RegExp, file: string): string[] {
  const source = readFileSync(file, "utf8");
  const matches = [...source.matchAll(new RegExp(pattern, pattern.flags))];
  // The count is asserted, because a pattern that stopped matching would otherwise leave this test
  // comparing two empty sets and passing.
  expect(matches, `${String(pattern)} matched nothing in ${file}`).not.toHaveLength(0);
  return matches.map((match) => match[1]);
}

function bundleKeys(): string[] {
  const source = readFileSync(bundleEntry, "utf8");
  const body = COMPONENT_MAP.exec(source);
  expect(body, `no component map found in ${bundleEntry}`).not.toBeNull();
  return [...body![1].matchAll(COMPONENT_MAP_KEY)].map((match) => match[1]);
}

test("every name the C# manifest advertises is a key this bundle registers", () => {
  const advertised = readAll(MANIFEST_COMPONENT_NAME, manifestSource);
  const registered = bundleKeys();

  expect(registered.length).toBeGreaterThan(0);
  expect([...advertised].sort()).toEqual([...registered].sort());
});
