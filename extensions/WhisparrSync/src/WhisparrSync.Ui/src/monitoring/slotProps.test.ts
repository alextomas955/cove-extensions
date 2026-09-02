/**
 * The one prop the slot control reads, pinned against the host's own slot context.
 *
 * The host's `Studio` type cannot be generated into this bundle's wire types, which are emitted from
 * this extension's own registrations, so the prop shape is hand-declared. Its field name is read out
 * of the host source where that source is present, because a name copied into this file would agree
 * with whichever side it was copied from and stop reporting the other.
 *
 * The host source is not always present: one CI leg builds with no Cove checkout at all. There the
 * transcribed name below is what is asserted, and the browser-side half is the containerized leg's.
 * The transcription is named in one place so the two halves cannot disagree about what they pin.
 *
 * A source pin rather than a DOM test, and in its own file for that reason: the rendering tests run
 * under jsdom, where the filesystem is not reachable.
 */
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { test, expect } from "vitest";

/** The field the host's studio action slot passes, transcribed by hand from that slot. */
const SLOT_FIELD = "studio";

const control = path.join(import.meta.dirname, "EntityMonitorButton.tsx");

/** Where the Cove checkout is, by the same precedence the build resolves it with. */
function hostStudioPage(): string | null {
  const repoRoot = path.resolve(import.meta.dirname, "..", "..", "..", "..", "..", "..");
  const candidates = [
    process.env.COVE_REPO,
    path.join(repoRoot, "cove"),
    path.join(repoRoot, "..", "cove"),
  ].filter((root): root is string => typeof root === "string" && root.length > 0);

  for (const root of candidates) {
    const page = path.join(root, "ui", "src", "pages", "StudioDetailPage.tsx");
    if (existsSync(page)) return page;
  }
  return null;
}

test("the host's studio action slot carries the field this pin names", () => {
  const page = hostStudioPage();
  if (page === null) {
    // No Cove checkout on this leg, so there is nothing to compare the transcription against.
    expect(SLOT_FIELD).toBe("studio");
    return;
  }

  const slot = /<ExtensionSlot\s+slot="studio-detail-actions"\s+context=\{\{([^}]*)\}\}/.exec(
    readFileSync(page, "utf8"),
  );

  expect(slot, `no studio-detail-actions slot found in ${page}`).not.toBeNull();

  const carried = slot![1].split(",").map((name) => name.trim().split(":")[0].trim());
  expect(carried).toContain(SLOT_FIELD);
});

test("the control declares that field at its narrowest and reads nothing else off it", () => {
  const source = readFileSync(control, "utf8");

  expect(
    new RegExp(
      `\\{\\s*${SLOT_FIELD}\\s*\\}:\\s*\\{\\s*${SLOT_FIELD}:\\s*\\{\\s*id:\\s*number\\s*\\}\\s*\\}`,
    ).test(source),
    `the control no longer declares exactly { ${SLOT_FIELD}: { id: number } }`,
  ).toBe(true);

  // The Cove id and nothing else. The host object also carries the library's own identity rows, and
  // the identifier the instance is given is re-resolved from them ON THE SERVER; a browser reading
  // one would be naming the entity a third party is asked about, which the product forbids outright.
  // Asserted as the whole set of fields read rather than as the absence of one name, so a field
  // nobody thought to forbid fails here too.
  const read = [
    ...new Set(
      [...source.matchAll(new RegExp(`\\b${SLOT_FIELD}\\.([A-Za-z_$][\\w$]*)`, "g"))].map(
        (match) => match[1],
      ),
    ),
  ];
  expect(read).toEqual(["id"]);
});
