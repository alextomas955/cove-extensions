/**
 * Four properties of the bundle's user-facing copy that no reviewer catches reliably by eye.
 *
 * Every expectation below is a literal array transcribed by hand from the spec. An expectation
 * computed from the module it checks agrees with itself forever and reports nothing.
 */
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

import * as copy from "./copy";
import { describeRefusal, REFUSAL_KINDS } from "./refusalLogic";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SRC = path.resolve(HERE, "../..");

/**
 * Whisparr's two generations carry different entity models. The split is an implementation fact and
 * must never reach a user's eyes, so none of these words may appear in shipped copy.
 */
const MODEL_SPLIT_VOCABULARY = ["movie", "series", "episode", "season"];

/**
 * Phrasings the spec forbids outright: they either name a generic failure where the product owes a
 * specific one, or they advise changing which product version is installed.
 */
const FORBIDDEN_ANYWHERE = [
  "unsupported",
  "not supported",
  "upgrade to",
  "upgrade your",
  "migrate",
  "migrating",
  "downgrade",
  "newer version",
  "older version",
];

/**
 * Phrasings that point the reader at a setting. Forbidden for a capability the connected generation
 * lacks, where changing a setting would not enable it and the advice sends the reader somewhere that
 * cannot help. Deliberately NOT applied to every constant: the spec's own affordance for the
 * not-configured kind is to name the setting and where to set it.
 */
const FORBIDDEN_IN_A_CAPABILITY_GAP = [
  "setting",
  "settings",
  "enable",
  "turn on",
  "configure",
  "preferences",
];

/** Sentences declared here for a surface that arrives in a later plan, and rendered by nothing yet. */
const DECLARED_FOR_LATER = ["SEARCH_WITH_NO_ENTRY", "IMPORTS_UNREADABLE", "WHISPARR_MAY_RENAME"];

/** Sentences the connect surface reads through its own kind table. */
const RENDERED_BY_THE_CONNECT_SURFACE = ["CONNECT_NOT_CONFIGURED", "CONNECT_KEY_REJECTED"];

/**
 * The kind whose sentence is the surface's to write, because the spec's affordance for it is to name
 * the surface's own setting. Every other kind carries a specified sentence.
 */
const SENTENCE_SUPPLIED_BY_THE_SURFACE = ["notConfigured"];

/** Every string constant `copy.ts` exports, by name. The sentence-building functions are skipped. */
const CONSTANTS: [string, string][] = Object.entries(copy).flatMap(([name, value]) =>
  typeof value === "string" ? [[name, value] as [string, string]] : [],
);

/** Every `.ts`/`.tsx` under `src/`, excluding this file. */
function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return sourceFiles(full);
    if (!/\.tsx?$/.test(entry.name)) return [];
    return full === fileURLToPath(import.meta.url) ? [] : [full];
  });
}

function occurrences(text: string, needle: string): number {
  let count = 0;
  for (let i = text.indexOf(needle); i !== -1; i = text.indexOf(needle, i + needle.length)) {
    count += 1;
  }
  return count;
}

function contains(sentence: string, phrase: string): boolean {
  return new RegExp(`\\b${phrase}`, "i").test(sentence);
}

describe("the version-gap sentence is single-sourced", () => {
  it("is declared exactly once across the shipped bundle", () => {
    // A test's own hand-transcribed pin is an expectation, not a second declaration, so the count is
    // taken over shipped source only.
    const declarations = sourceFiles(SRC)
      .filter((file) => !/\.test\.tsx?$/.test(file))
      .map((file) => ({
        file: path.relative(SRC, file),
        count: occurrences(readFileSync(file, "utf8"), copy.CAP_UNAVAILABLE_ON_THIS_GENERATION),
      }))
      .filter((entry) => entry.count > 0);

    expect(declarations).toEqual([{ file: path.join("common", "ui", "copy.ts"), count: 1 }]);
  });
});

describe("the two generations' entity models never reach a user's eyes", () => {
  it("keeps the model-split vocabulary out of every copy constant", () => {
    for (const [name, sentence] of CONSTANTS) {
      for (const word of MODEL_SPLIT_VOCABULARY) {
        expect(contains(sentence, word), `${name} names "${word}"`).toBe(false);
      }
    }
  });

  it("has something to check", () => {
    expect(CONSTANTS.length).toBeGreaterThan(0);
  });
});

describe("a capability gap is never worded as a fault or a fix", () => {
  it("keeps the forbidden phrasings out of every copy constant", () => {
    for (const [name, sentence] of CONSTANTS) {
      for (const phrase of FORBIDDEN_ANYWHERE) {
        expect(contains(sentence, phrase), `${name} says "${phrase}"`).toBe(false);
      }
    }
  });

  it("points the reader at no setting for a capability the generation does not have", () => {
    const sentence = describeRefusal("versionCapability").sentence;
    expect(sentence).not.toBeNull();
    for (const phrase of FORBIDDEN_IN_A_CAPABILITY_GAP) {
      expect(contains(sentence ?? "", phrase), `the version gap says "${phrase}"`).toBe(false);
    }
  });
});

describe("no sentence is orphaned and no kind is silent", () => {
  it("gives every kind a specified sentence, or names it as the surface's to write", () => {
    for (const kind of REFUSAL_KINDS) {
      const hasSentence = describeRefusal(kind).sentence !== null;
      expect(hasSentence, kind).toBe(!SENTENCE_SUPPLIED_BY_THE_SURFACE.includes(kind));
    }
  });

  it("accounts for every declared sentence", () => {
    const fromAKind = new Set(
      REFUSAL_KINDS.map((kind) => describeRefusal(kind).sentence).filter(
        (sentence) => sentence !== null,
      ),
    );
    const accountedByName = [...DECLARED_FOR_LATER, ...RENDERED_BY_THE_CONNECT_SURFACE];

    const orphans = CONSTANTS.filter(
      ([name, sentence]) => !fromAKind.has(sentence) && !accountedByName.includes(name),
    ).map(([name]) => name);

    expect(orphans).toEqual([]);
  });

  it("names no sentence that no longer exists", () => {
    const declared = CONSTANTS.map(([name]) => name);
    for (const name of [...DECLARED_FOR_LATER, ...RENDERED_BY_THE_CONNECT_SURFACE]) {
      expect(declared, name).toContain(name);
    }
  });
});
