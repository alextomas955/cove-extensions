/**
 * The three facts about this surface that travel outside a request or response body, so no generated
 * type carries either side of them: the prop name each host card slot passes, the card kinds the
 * status route answers for, and the most identifiers one status request may carry.
 *
 * Each is read out of the file that declares it rather than transcribed, so a change on one side
 * reports here instead of answering a bad request. The host source is not always present: one CI leg
 * builds with no Cove checkout at all, and there the slot half has nothing to compare against.
 *
 * What the badges send and draw is `CardStatusBadge.test.ts`'s, and what the route accepts is the
 * backend suite's. This file is separate from both because the rendering tests run under jsdom,
 * where the filesystem is not reachable.
 */
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { expect, test } from "vitest";

/**
 * Each card slot, the field it passes, and the host source that draws it.
 *
 * The host draws its card slots in two places: a list page for an entity card, and the shared card
 * component for a scene card. Each entry carries its own path under the host's `ui/src` for that
 * reason.
 */
const SLOTS = [
  { slot: "studio-card-footer", field: "studio", source: ["pages", "StudiosPage.tsx"] },
  { slot: "performer-card-footer", field: "performer", source: ["pages", "PerformersPage.tsx"] },
  { slot: "video-card-content", field: "video", source: ["components", "EntityCards.tsx"] },
];

const store = path.join(import.meta.dirname, "cardStatusStore.ts");

/** A file of the server project, which is in this repo and present on every leg. */
function serverSource(...under: string[]): string {
  return path.resolve(import.meta.dirname, "..", "..", "..", "WhisparrSync", ...under);
}

const contracts = serverSource("Contracts", "LibraryStatusContracts.cs");

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

test("the identifiers one status request carries are the most the route accepts", () => {
  // The bound reaches no request or response body, so it cannot be generated into this bundle's wire
  // types. A figure transcribed here would agree with itself while the route refused every page.
  // That the route enforces this figure rather than one of its own is the backend suite's:
  // LibraryStatusRouteTests serves a body at the bound and refuses one over it.
  const declaring = serverSource("WhisparrSync.Missing.cs");
  const bound = /private const int MissingPerPage = (\d+);/.exec(readFileSync(declaring, "utf8"));
  expect(bound, `no page bound found in ${declaring}`).not.toBeNull();

  const browser = readFileSync(path.join(import.meta.dirname, "cardStatusStore.ts"), "utf8");
  const sending = /const IDS_PER_REQUEST = (\d+);/.exec(browser);
  expect(sending, "no IDS_PER_REQUEST found in cardStatusStore.ts").not.toBeNull();

  expect(Number(sending![1]), "the browser sends more identifiers than the route accepts").toBe(
    Number(bound![1]),
  );
});
