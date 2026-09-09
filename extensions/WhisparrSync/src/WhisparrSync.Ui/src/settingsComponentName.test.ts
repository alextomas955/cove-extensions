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

/**
 * Every `componentName:` argument in the manifest override, whether it is written as a literal or
 * as a constant the same file declares.
 *
 * Both forms, because a registration naming a constant is exactly as binding as one naming a
 * literal: a pattern seeing only literals would report nothing about it and leave a component the
 * host asks for and the bundle never registers.
 */
const MANIFEST_COMPONENT_NAME_LITERAL = /componentName:\s*"([^"]*)"/g;

/** Each `componentName:` argument written as a constant this same file declares. */
const MANIFEST_COMPONENT_NAME_CONSTANT = /componentName:\s*(\w+)/g;

/** The value a `const string NAME = "…"` declaration in the manifest override carries. */
const MANIFEST_STRING_CONSTANT = (name: string) =>
  new RegExp(`const\\s+string\\s+${name}\\s*=\\s*"([^"]*)"`);

/** The whole body of the bundle's `actionHandlers` map. */
const ACTION_HANDLER_MAP = /actionHandlers\s*=\s*\{([^}]*)\}/;

/**
 * Every `handlerName:` argument the manifest passes, whether written as a literal or as a constant
 * the same file declares.
 *
 * Read from the registrations rather than from one named constant: a pattern naming a constant
 * reports nothing about a second action registered under a second one, and an action the host can
 * dispatch that the bundle never registers is exactly what this compares.
 */
const MANIFEST_HANDLER_NAME_LITERAL = /handlerName:\s*"([^"]*)"/g;

/** Each `handlerName:` argument written as a constant this same file declares. */
const MANIFEST_HANDLER_NAME_CONSTANT = /handlerName:\s*(\w+)/g;

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

function handlerKeys(): string[] {
  const source = readFileSync(bundleEntry, "utf8");
  const body = ACTION_HANDLER_MAP.exec(source);
  expect(body, `no action-handler map found in ${bundleEntry}`).not.toBeNull();
  return [...body![1].matchAll(COMPONENT_MAP_KEY)].map((match) => match[1]);
}

/**
 * Every component name the manifest advertises, with any constant resolved to its value.
 *
 * Distinct names. One component is advertised once per page type it is registered on, and this
 * file's claim is about which NAMES the host can ask for rather than how many registrations name
 * each one.
 */
function advertisedComponentNames(): string[] {
  const source = readFileSync(manifestSource, "utf8");
  const named = [
    ...readAll(MANIFEST_COMPONENT_NAME_LITERAL, manifestSource),
    ...[...source.matchAll(MANIFEST_COMPONENT_NAME_CONSTANT)].map((match) => {
      const declared = MANIFEST_STRING_CONSTANT(match[1]).exec(source);
      expect(declared, `${match[1]} is not declared in ${manifestSource}`).not.toBeNull();
      return declared![1];
    }),
  ];

  return [...new Set(named)];
}

/** Every handler name the manifest declares, with any constant resolved to its value. */
function declaredHandlerNames(): string[] {
  const source = readFileSync(manifestSource, "utf8");
  const named = [
    ...[...source.matchAll(MANIFEST_HANDLER_NAME_LITERAL)].map((match) => match[1]),
    ...readAll(MANIFEST_HANDLER_NAME_CONSTANT, manifestSource).map((name) => {
      const declared = MANIFEST_STRING_CONSTANT(name).exec(source);
      expect(declared, `${name} is not declared in ${manifestSource}`).not.toBeNull();
      return declared![1];
    }),
  ];

  return [...new Set(named)];
}

test("every name the C# manifest advertises is a key this bundle registers", () => {
  const advertised = advertisedComponentNames();
  const registered = bundleKeys();

  expect(registered.length).toBeGreaterThan(0);
  expect([...advertised].sort()).toEqual([...registered].sort());
});

/**
 * The same exact-string rule governs an action handler. A key that does not match the manifest's
 * handler name leaves the host dispatching nothing when the bulk button is pressed, with no error.
 */
test("every handler name the C# manifest declares is a key this bundle registers", () => {
  const declared = declaredHandlerNames();
  const registered = handlerKeys();

  expect(registered.length).toBeGreaterThan(0);
  expect([...declared].sort()).toEqual([...registered].sort());
});
