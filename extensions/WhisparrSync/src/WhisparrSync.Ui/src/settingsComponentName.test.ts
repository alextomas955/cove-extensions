/**
 * The one string that spans both tiers: the `componentName` the C# manifest advertises and the key
 * this bundle registers its page under. The host resolves one to the other by exact string, and
 * renders nothing with no error anywhere when they differ, so neither side can detect a drift alone.
 *
 * Each name is read from the tier that owns it, never from a literal here: a value copied into this
 * file would agree with whichever side it was copied from and stop reporting the other.
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

/** The sole key of `defineExtension({ components: { … } })` in the bundle entry. */
const COMPONENT_MAP_KEY = /components:\s*\{\s*([A-Za-z_$][\w$]*)\s*\}/;

/** The `componentName:` argument of the `AddSettingsSection` call in the manifest override. */
const MANIFEST_COMPONENT_NAME = /componentName:\s*"([^"]*)"/;

function readOnly(pattern: RegExp, file: string): string {
  const source = readFileSync(file, "utf8");
  const matches = [...source.matchAll(new RegExp(pattern, "g"))];
  // Both counts are asserted, because a pattern that stopped matching would otherwise leave this
  // test comparing nothing and passing, and a second match means the assertion below picks one of
  // two names arbitrarily.
  expect(
    matches,
    `${pattern} matched ${matches.length} time(s) in ${file}, expected exactly 1`,
  ).toHaveLength(1);
  return matches[0][1];
}

test("the bundle's component-map key is the name the C# manifest advertises", () => {
  expect(readOnly(COMPONENT_MAP_KEY, bundleEntry)).toBe(
    readOnly(MANIFEST_COMPONENT_NAME, manifestSource),
  );
});
