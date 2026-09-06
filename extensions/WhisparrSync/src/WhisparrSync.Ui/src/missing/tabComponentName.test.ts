/**
 * The tab registration binds two strings across two tiers: the manifest's `componentName` and the
 * key this bundle registers a component under. The host resolves one to the other by exact string
 * and renders nothing, with no error anywhere, when they differ.
 *
 * Both names are read from the file that owns them, never written as literals here: a value copied
 * into this file would agree with whichever side it was copied from and stop reporting the other.
 * The three page TYPES are literals, because they belong to the host's checkout rather than to this
 * repository, so there is no owning file here to read them from.
 *
 * A source pin rather than a DOM test, and in its own file for that reason: the rendering tests run
 * under jsdom, where the filesystem is not reachable.
 */
import { readFileSync } from "node:fs";
import path from "node:path";
import { expect, test } from "vitest";

/** The three host page types this tab is registered on, transcribed from the host that draws them. */
const HOST_PAGE_TYPES = ["studio", "performer", "tag"];

const bundleEntry = path.resolve(import.meta.dirname, "..", "index.ts");
const manifestSource = path.resolve(
  import.meta.dirname,
  "..",
  "..",
  "..",
  "WhisparrSync",
  "WhisparrSync.Api.cs",
);

/** Each `AddTab` registration in the manifest override, as its page type and its component name. */
const MANIFEST_TAB = /\.AddTab\(\s*pageType:\s*"([^"]*)"[\s\S]*?componentName:\s*(\w+)/g;

/** The constant the manifest's `componentName` argument names, and its value. */
const COMPONENT_NAME_CONSTANT = (name: string) =>
  new RegExp(`const\\s+string\\s+${name}\\s*=\\s*"([^"]*)"`);

/** The whole body of `defineExtension({ components: { … } })` in the bundle entry. */
const COMPONENT_MAP = /components:\s*\{([^}]*)\}/;

function registeredTabs() {
  const source = readFileSync(manifestSource, "utf8");
  return [...source.matchAll(MANIFEST_TAB)].map((match) => {
    const declared = COMPONENT_NAME_CONSTANT(match[2]).exec(source);
    expect(declared, `${match[2]} is not declared in ${manifestSource}`).not.toBeNull();
    return { pageType: match[1], componentName: declared![1] };
  });
}

function componentMapBody() {
  const body = COMPONENT_MAP.exec(readFileSync(bundleEntry, "utf8"));
  expect(body, `no component map found in ${bundleEntry}`).not.toBeNull();
  return body![1];
}

test("the manifest registers the tab on each of the three host page types and no others", () => {
  const tabs = registeredTabs();

  // The count is asserted before the names, because a pattern that stopped matching would otherwise
  // leave the comparison below reading two empty sets and passing.
  expect(tabs, `${String(MANIFEST_TAB)} matched nothing in ${manifestSource}`).toHaveLength(
    HOST_PAGE_TYPES.length,
  );
  expect(tabs.map(({ pageType }) => pageType).sort()).toEqual([...HOST_PAGE_TYPES].sort());
});

test("the component every tab names is a key this bundle registers", () => {
  const body = componentMapBody();

  for (const { pageType, componentName } of registeredTabs()) {
    // Key position specifically. A name appearing only as a VALUE would be a component the host is
    // never able to ask for under the name the manifest advertises.
    const inKeyPosition = new RegExp(`(^|,)\\s*${componentName}\\s*(,|:|$)`).test(body);
    expect(
      inKeyPosition,
      `the ${pageType} tab names ${componentName}, which the bundle does not register`,
    ).toBe(true);
  }
});

test("the bundle registers exactly the four components this extension advertises", () => {
  const keys = componentMapBody()
    .split(",")
    .map((entry) => entry.split(":")[0].trim())
    .filter((key) => key !== "");

  expect(keys).toHaveLength(4);
  expect(keys).toContain(registeredTabs()[0].componentName);
});
