/**
 * The two entity-action slots this extension registers, and the components they name.
 *
 * A slot registration binds three strings across two repositories: the host's slot name, the
 * manifest's `componentName`, and the key this bundle registers a component under. The host resolves
 * each by exact string and renders nothing, with no error anywhere, when any pair differs, so a
 * control that silently never appears is the failure this file exists to report.
 *
 * The component names are read from the tier that owns them, never written as literals here: a value
 * copied into this file would agree with whichever side it was copied from and stop reporting the
 * other. The slot NAMES are the exception and ARE literals, because they belong to the host's
 * checkout rather than to this repository, so there is no owning file here to read them from. A
 * wrong transcription passes this test and shows up as a blank control in the containerized e2e leg,
 * which is why that leg is the gate for whether the host actually renders the control.
 *
 * A source pin rather than a DOM test, and in its own file for that reason: the rendering tests run
 * under jsdom, where the filesystem is not reachable.
 */
import { readFileSync } from "node:fs";
import path from "node:path";
import { test, expect } from "vitest";

/** The host's action-row slot on the studio detail page, transcribed from the host that renders it. */
const STUDIO_ACTION_SLOT = "studio-detail-actions";

/** The host's action-row slot on the performer detail page, transcribed the same way. */
const PERFORMER_ACTION_SLOT = "performer-detail-actions";

/**
 * Every entity-action slot this extension is expected to occupy: the host offers exactly one such
 * position per detail page, and this extension puts one control in each.
 */
const HOST_ENTITY_ACTION_SLOTS = [STUDIO_ACTION_SLOT, PERFORMER_ACTION_SLOT];

const bundleEntry = path.resolve(import.meta.dirname, "..", "index.ts");
const manifestSource = path.resolve(
  import.meta.dirname,
  "..",
  "..",
  "..",
  "WhisparrSync",
  "WhisparrSync.Api.cs",
);

/** Each `AddSlot` registration in the manifest override, as its slot name and its component name. */
const MANIFEST_SLOT = /\.AddSlot\(\s*"([^"]*)"\s*,\s*componentName:\s*"([^"]*)"/g;

/** The whole body of `defineExtension({ components: { … } })` in the bundle entry. */
const COMPONENT_MAP = /components:\s*\{([^}]*)\}/;

function registeredSlots(): { slot: string; componentName: string }[] {
  const source = readFileSync(manifestSource, "utf8");
  return [...source.matchAll(MANIFEST_SLOT)].map((match) => ({
    slot: match[1],
    componentName: match[2],
  }));
}

/** The text between the braces of the bundle's component map, where every key is in key position. */
function componentMapBody(): string {
  const body = COMPONENT_MAP.exec(readFileSync(bundleEntry, "utf8"));
  expect(body, `no component map found in ${bundleEntry}`).not.toBeNull();
  return body![1];
}

test("the manifest registers one entity-action slot per detail page and no others", () => {
  const slots = registeredSlots();

  // The count is asserted before the names, because a pattern that stopped matching would otherwise
  // leave the comparison below reading two empty sets and passing.
  expect(slots, `${String(MANIFEST_SLOT)} matched nothing in ${manifestSource}`).toHaveLength(
    HOST_ENTITY_ACTION_SLOTS.length,
  );
  expect(slots.map(({ slot }) => slot).sort()).toEqual([...HOST_ENTITY_ACTION_SLOTS].sort());
});

test("every component a slot names is a key this bundle registers", () => {
  const body = componentMapBody();

  for (const { slot, componentName } of registeredSlots()) {
    // Key position specifically. A name appearing only as a VALUE would be a component the host is
    // never able to ask for under the name the manifest advertises.
    const inKeyPosition = new RegExp(`(^|,)\\s*${componentName}\\s*(,|:|$)`).test(body);
    expect(
      inKeyPosition,
      `${slot} names ${componentName}, which the bundle does not register`,
    ).toBe(true);
  }
});
