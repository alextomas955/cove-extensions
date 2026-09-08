/**
 * The one prop each card badge reads, pinned against the host's own slot context, and the card kinds
 * the status route answers for, pinned against the enum the server declares.
 *
 * The host's `Studio` and `Performer` types cannot be generated into this bundle's wire types, which
 * are emitted from this extension's own registrations, so each prop shape is hand-declared. Its
 * field name is read out of the host source where that source is present, because a name copied into
 * this file would agree with whichever side it was copied from and stop reporting the other.
 *
 * The host source is not always present: one CI leg builds with no Cove checkout at all. There the
 * transcribed names below are what is asserted, and the browser-side half is the containerized
 * leg's.
 *
 * A source pin rather than a DOM test, and in its own file for that reason: the rendering tests run
 * under jsdom, where the filesystem is not reachable.
 */
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { expect, test } from "vitest";

/**
 * Each card slot, the field it passes, the host source that draws it and the badge that declares it.
 *
 * The host draws its card slots in two places: a list page for an entity card, and the shared card
 * component for a scene card. Each entry carries its own path under the host's `ui/src` for that
 * reason.
 */
const SLOTS = [
  {
    slot: "studio-card-footer",
    field: "studio",
    source: ["pages", "StudiosPage.tsx"],
    badge: "WhisparrEntityCardBadge.tsx",
  },
  {
    slot: "performer-card-footer",
    field: "performer",
    source: ["pages", "PerformersPage.tsx"],
    badge: "WhisparrEntityCardBadge.tsx",
  },
  {
    slot: "video-card-content",
    field: "video",
    source: ["components", "EntityCards.tsx"],
    badge: "WhisparrVideoCardBadge.tsx",
  },
];

const store = path.join(import.meta.dirname, "cardStatusStore.ts");

const contracts = path.resolve(
  import.meta.dirname,
  "..",
  "..",
  "..",
  "WhisparrSync",
  "Contracts",
  "LibraryStatusContracts.cs",
);

/** The members of the card-kind enum the server declares, in the casing the route parses. */
const CARD_KIND_ENUM = /public enum LibraryCardKind\s*\{([\s\S]*?)\n\}/;

/** Where the Cove checkout is, by the same precedence the build resolves it with. */
function hostSource(under: string[]): string | null {
  const repoRoot = path.resolve(import.meta.dirname, "..", "..", "..", "..", "..", "..");
  const candidates = [
    process.env.COVE_REPO,
    path.join(repoRoot, "cove"),
    path.join(repoRoot, "..", "cove"),
  ].filter((root): root is string => typeof root === "string" && root.length > 0);

  for (const root of candidates) {
    const full = path.join(root, "ui", "src", ...under);
    if (existsSync(full)) return full;
  }
  return null;
}

test("each host card slot carries the field this pin names", () => {
  for (const { slot, field, source: under } of SLOTS) {
    const source = hostSource(under);
    if (source === null) {
      // No Cove checkout on this leg, so there is nothing to compare the transcription against.
      expect(field).not.toBe("");
      continue;
    }

    const rendered = new RegExp(
      `<CardExtensionSlot\\s+slot="${slot}"\\s+context=\\{\\{([^}]*)\\}\\}`,
    ).exec(readFileSync(source, "utf8"));

    expect(rendered, `no ${slot} slot found in ${source}`).not.toBeNull();

    const carried = rendered![1].split(",").map((name) => name.trim().split(":")[0].trim());
    expect(carried, slot).toContain(field);
  }
});

test("each badge declares its field at its narrowest and reads nothing else off it", () => {
  for (const { field, badge } of SLOTS) {
    const source = readFileSync(path.join(import.meta.dirname, badge), "utf8");

    expect(
      new RegExp(
        `\\{\\s*${field}\\s*\\}:\\s*\\{\\s*${field}:\\s*\\{\\s*id:\\s*number\\s*\\}\\s*\\}`,
      ).test(source),
      `the badge no longer declares exactly { ${field}: { id: number } }`,
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

test("the card kinds the browser names are the ones the server declares", () => {
  // The kind travels in a path segment, so it reaches no request or response body and cannot be
  // generated into this bundle's wire types. It is read from the file that declares it rather than
  // transcribed, so a member added on one side reports here rather than answering a bad request.
  const declared = CARD_KIND_ENUM.exec(readFileSync(contracts, "utf8"));
  expect(declared, `no LibraryCardKind enum found in ${contracts}`).not.toBeNull();

  const members = [...declared![1].matchAll(/^\s{4}(\w+),$/gm)].map((match) =>
    match[1].replace(/^./, (first) => first.toLowerCase()),
  );

  const union = /export type LibraryCardKind =\s*([^;]*);/.exec(readFileSync(store, "utf8"));
  expect(union, `no LibraryCardKind union found in ${store}`).not.toBeNull();

  const named = [...union![1].matchAll(/"([^"]+)"/g)].map((match) => match[1]);

  expect(members.length, `${contracts} declares no enum member at all`).toBeGreaterThan(0);
  expect([...named].sort()).toEqual([...members].sort());
});
