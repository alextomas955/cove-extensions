/**
 * The tab registration binds two strings across two tiers: the manifest's `componentName` and the
 * key this bundle registers a component under. The host resolves one to the other by exact string
 * and renders nothing, with no error anywhere, when they differ.
 *
 * Both names are read from the file that owns them, never written as literals here: a value copied
 * into this file would agree with whichever side it was copied from and stop reporting the other.
 * The page TYPES are literals, because they belong to the host's checkout rather than to this
 * repository, so there is no owning file here to read them from.
 *
 * A source pin rather than a DOM test, and in its own file for that reason: the rendering tests run
 * under jsdom, where the filesystem is not reachable.
 */
import { readFileSync } from "node:fs";
import path from "node:path";
import { expect, test } from "vitest";

/**
 * One entry per tab registration, naming the host page type it is made for, transcribed from the
 * host that draws them. The catalogue tab takes three of them and the scene tab takes the fourth.
 */
const HOST_PAGE_TYPES_WITH_A_TAB = ["studio", "performer", "tag", "video"];

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

/** Each `AddSettingsSection` registration in the manifest override, by component name. */
const MANIFEST_SETTINGS_SECTION = /\.AddSettingsSection\([\s\S]*?componentName:\s*"([^"]*)"/g;

/** Each `AddSlot` registration in the manifest override, by component name. */
const MANIFEST_SLOT_COMPONENT = /\.AddSlot\(\s*"[^"]*"\s*,\s*componentName:\s*"([^"]*)"/g;

/** Every component name the manifest advertises, however it advertises it. */
function advertisedComponents(): string[] {
  const source = readFileSync(manifestSource, "utf8");
  const named = [MANIFEST_SETTINGS_SECTION, MANIFEST_SLOT_COMPONENT].flatMap((pattern) =>
    [...source.matchAll(pattern)].map((match) => match[1]),
  );
  return [...new Set([...named, ...registeredTabs().map((tab) => tab.componentName)])];
}

function componentMapBody() {
  const body = COMPONENT_MAP.exec(readFileSync(bundleEntry, "utf8"));
  expect(body, `no component map found in ${bundleEntry}`).not.toBeNull();
  return body![1];
}

test("the manifest registers a tab on each host page type it names and no others", () => {
  const tabs = registeredTabs();

  // The count is asserted before the names, because a pattern that stopped matching would otherwise
  // leave the comparison below reading two empty sets and passing.
  expect(tabs, `${String(MANIFEST_TAB)} matched nothing in ${manifestSource}`).toHaveLength(
    HOST_PAGE_TYPES_WITH_A_TAB.length,
  );
  expect(tabs.map(({ pageType }) => pageType).sort()).toEqual(
    [...HOST_PAGE_TYPES_WITH_A_TAB].sort(),
  );
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

test("the bundle registers exactly the components this extension advertises", () => {
  const keys = componentMapBody()
    .split(",")
    .map((entry) => entry.split(":")[0].trim())
    .filter((key) => key !== "");

  const advertised = advertisedComponents();

  // Both directions. A key the manifest never names is a component the host can never ask for, and a
  // name the bundle never registers is a surface that renders nothing with no error anywhere.
  expect(advertised.length, `${manifestSource} advertises no component at all`).toBeGreaterThan(0);
  expect([...keys].sort()).toEqual([...advertised].sort());
});
