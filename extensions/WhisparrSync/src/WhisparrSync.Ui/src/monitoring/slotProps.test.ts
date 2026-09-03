/**
 * The one prop each slot control reads, pinned against the host's own slot context.
 *
 * The host's `Studio` and `Performer` types cannot be generated into this bundle's wire types, which
 * are emitted from this extension's own registrations, so each prop shape is hand-declared. Its
 * field name is read out of the host source where that source is present, because a name copied into
 * this file would agree with whichever side it was copied from and stop reporting the other.
 *
 * The host source is not always present: one CI leg builds with no Cove checkout at all. There the
 * transcribed names below are what is asserted, and the browser-side half is the containerized
 * leg's. Each transcription is named in one place so the two halves cannot disagree about what they
 * pin.
 *
 * A source pin rather than a DOM test, and in its own file for that reason: the rendering tests run
 * under jsdom, where the filesystem is not reachable.
 */
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { test, expect } from "vitest";

/** Each slot, and the field it passes, transcribed by hand from the host page that renders it. */
const SLOTS = [
  { slot: "studio-detail-actions", field: "studio", page: "StudioDetailPage.tsx" },
  { slot: "performer-detail-actions", field: "performer", page: "PerformerDetailPage.tsx" },
];

const control = path.join(import.meta.dirname, "EntityMonitorButton.tsx");

/** Where the Cove checkout is, by the same precedence the build resolves it with. */
function hostPage(page: string): string | null {
  const repoRoot = path.resolve(import.meta.dirname, "..", "..", "..", "..", "..", "..");
  const candidates = [
    process.env.COVE_REPO,
    path.join(repoRoot, "cove"),
    path.join(repoRoot, "..", "cove"),
  ].filter((root): root is string => typeof root === "string" && root.length > 0);

  for (const root of candidates) {
    const full = path.join(root, "ui", "src", "pages", page);
    if (existsSync(full)) return full;
  }
  return null;
}

test("each host action slot carries the field this pin names", () => {
  for (const { slot, field, page } of SLOTS) {
    const source = hostPage(page);
    if (source === null) {
      // No Cove checkout on this leg, so there is nothing to compare the transcription against.
      expect(field).not.toBe("");
      continue;
    }

    const rendered = new RegExp(
      `<ExtensionSlot\\s+slot="${slot}"\\s+context=\\{\\{([^}]*)\\}\\}`,
    ).exec(readFileSync(source, "utf8"));

    expect(rendered, `no ${slot} slot found in ${source}`).not.toBeNull();

    const carried = rendered![1].split(",").map((name) => name.trim().split(":")[0].trim());
    expect(carried, slot).toContain(field);
  }
});

test("each control declares its field at its narrowest and reads nothing else off it", () => {
  const source = readFileSync(control, "utf8");

  for (const { field } of SLOTS) {
    expect(
      new RegExp(
        `\\{\\s*${field}\\s*\\}:\\s*\\{\\s*${field}:\\s*\\{\\s*id:\\s*number\\s*\\}\\s*\\}`,
      ).test(source),
      `the control no longer declares exactly { ${field}: { id: number } }`,
    ).toBe(true);

    // The Cove id and nothing else. The host object also carries the library's own identity rows,
    // and the identifier the instance is given is re-resolved from them ON THE SERVER; a browser
    // reading one would be naming the entity a third party is asked about, which the product forbids
    // outright. Asserted as the whole set of fields read rather than as the absence of one name, so
    // a field nobody thought to forbid fails here too.
    const read = [
      ...new Set(
        [...source.matchAll(new RegExp(`\\b${field}\\.([A-Za-z_$][\\w$]*)`, "g"))].map(
          (match) => match[1],
        ),
      ),
    ];
    expect(read, field).toEqual(["id"]);
  }
});
