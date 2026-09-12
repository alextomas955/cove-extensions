/**
 * The properties of the bundle's user-facing copy that no reviewer catches reliably by eye: the
 * words no sentence may use, the sentence that must be declared once, and what each formatted line
 * reads at the sizes that change its wording.
 *
 * Every expectation below is transcribed by hand from the spec. One computed from the module it
 * checks agrees with itself forever and reports nothing.
 *
 * Whether a sentence reaches the page is a property of a rendered surface, so it is asserted in the
 * component suite that draws it rather than in a list here.
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

describe("neither metadata source is named in the bundle", () => {
  /**
   * Which source answers follows the connected generation, so a name written here is a name that is
   * wrong on the other generation. The answered page carries it.
   */
  const SOURCE_NAMES = ["StashDB", "ThePornDB"];

  it("writes neither name into shipped source", () => {
    const named = sourceFiles(SRC)
      .filter((file) => !/\.test\.tsx?$/.test(file))
      .flatMap((file) => {
        const text = readFileSync(file, "utf8");
        return SOURCE_NAMES.filter((name) => text.includes(name)).map(
          (name) => `${path.relative(SRC, file)} names ${name}`,
        );
      });

    expect(named).toEqual([]);
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

describe("no refusal kind is silent", () => {
  it("gives every kind a specified sentence, or names it as the surface's to write", () => {
    for (const kind of REFUSAL_KINDS) {
      const hasSentence = describeRefusal(kind).sentence !== null;
      expect(hasSentence, kind).toBe(!SENTENCE_SUPPLIED_BY_THE_SURFACE.includes(kind));
    }
  });
});

describe("the count line says whether its total is a count or a floor", () => {
  it("marks a total the provider will not serve past, and leaves a real one unmarked", () => {
    expect(copy.countLine(1, 40, 10000, true).endsWith("+")).toBe(true);
    expect(copy.countLine(1, 40, 272, false).endsWith("+")).toBe(false);
  });

  it("names the range and the total", () => {
    expect(copy.countLine(41, 80, 272, false)).toBe("41–80 of 272");
  });
});

describe("the whole-catalogue confirmation names the figure and what it is not", () => {
  it("names the catalogue's size, the source, and that the held scenes are not in the run", () => {
    expect(copy.monitorAllConfirmation(665, "a source")).toBe(
      "This covers all 665 scenes a source lists here, minus the ones you already have. " +
        "Marking a scene wanted downloads nothing by itself.",
    );
  });

  it("reads as one scene at one, so the wording assumes no plural", () => {
    expect(copy.monitorAllConfirmation(1, "a source")).toContain("the 1 scene a source lists here");
  });

  /**
   * The point of confirming at all. A gesture reaching a whole catalogue reads as a download of that
   * size, and the sentence is the only thing that says it is not.
   */
  it("says the marking downloads nothing, at every size", () => {
    for (const count of [0, 1, 665]) {
      expect(copy.monitorAllConfirmation(count, "a source")).toContain(
        copy.MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF,
      );
    }
  });
});

describe("a facet with nothing picked names what its menu covers", () => {
  it("reads as one phrase whatever case the source spelled the menu's name in", () => {
    expect(copy.facetCoversEverything("Tags")).toBe("All tags");
    expect(copy.facetCoversEverything("Performers")).toBe("All performers");
  });
});

describe("the selection count reads at zero, one and many", () => {
  it("names the number and nothing that has to agree with it", () => {
    // No noun after the number, which is the host selection bar's own wording, so there is no plural
    // form to disagree with the count.
    expect(copy.selectionCount(0)).toBe("0 selected");
    expect(copy.selectionCount(1)).toBe("1 selected");
    expect(copy.selectionCount(2)).toBe("2 selected");
  });
});

describe("the confirmation names what the choice covers and what it costs", () => {
  it("names one entity at one, and both consequences where the choice cannot be taken back", () => {
    const message = copy.allScenesConfirmation(1, true);

    expect(message).toContain("1 entity.");
    expect(message).toContain(copy.ALL_SCENES_MARKS_THE_BACK_CATALOGUE);
    expect(message).toContain(copy.ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE);
  });

  it("names the count at many, and leaves out the door that is not one", () => {
    const message = copy.allScenesConfirmation(12, false);

    expect(message).toContain("12 entities.");
    expect(message).toContain(copy.ALL_SCENES_MARKS_THE_BACK_CATALOGUE);
    expect(message).not.toContain(copy.ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE);
  });

  it("names what the search covers and that it takes files in", () => {
    expect(copy.searchAllMonitoredConfirmation(1)).toContain("1 entity.");

    const message = copy.searchAllMonitoredConfirmation(40);

    expect(message).toContain("40 entities.");
    expect(message).toContain(copy.SEARCH_ALL_MONITORED_SPENDS_TRAFFIC_AND_DISK);

    // The scope's own consequences are not restated here. The search writes no flag at all.
    expect(message).not.toContain(copy.ALL_SCENES_MARKS_THE_BACK_CATALOGUE);
  });
});

describe("the bound the over-the-bound sentence names is the server's own", () => {
  /** The route that declares it, read as text: the bound is a C# constant with no wire spelling. */
  const ROUTES = path.resolve(SRC, "../../WhisparrSync/WhisparrSync.Api.cs");

  it("names the number the route refuses above", () => {
    const declared = /MaxEntityIdsPerRequest\s*=\s*(\d+)/.exec(readFileSync(ROUTES, "utf8"));

    expect(declared, "the route declares no MaxEntityIdsPerRequest").not.toBeNull();
    expect(copy.MAX_ENTITY_IDS_PER_REQUEST).toBe(Number(declared?.[1]));
    expect(copy.BULK_SELECTION_IS_OVER_THE_BOUND).toContain(
      String(copy.MAX_ENTITY_IDS_PER_REQUEST),
    );
  });

  it("names the lower number the route refuses the search verb above", () => {
    // Its own pin rather than a second read of the first. The sentence above names one limit and
    // cannot describe this one, so reusing it for the search row is what this catches.
    const declared = /MaxSceneSearchIdsPerRequest\s*=\s*(\d+)/.exec(readFileSync(ROUTES, "utf8"));

    expect(declared, "the route declares no MaxSceneSearchIdsPerRequest").not.toBeNull();
    expect(copy.MAX_SCENE_SEARCH_IDS_PER_REQUEST).toBe(Number(declared?.[1]));
    expect(copy.BATCH_SEARCH_IS_OVER_THE_BOUND).toContain(
      String(copy.MAX_SCENE_SEARCH_IDS_PER_REQUEST),
    );
  });
});

/**
 * Every sentence the sync section renders, by the answer it got when each was walked against what a
 * run does on the connection it is stated to.
 *
 * A run registers the scenes a reader owns, or the studios their library covers, and where the
 * monitor choice is on it marks scenes either way. So a sentence naming a scene where studios are
 * what gets registered is one defect this accounts for, and a sentence saying a studio is monitored
 * is the other. A `SYNC_` constant in none of the three lists reddens the suite below, which is what
 * makes a new sentence get the same walk.
 */
const RENDERED_BY_THE_SYNC_SECTION = {
  /** Names what the run registers, so it has one declaration per noun: the scene one, then the studio one. */
  pairedByWhatTheRunRegisters: [
    ["SYNC_REGISTERS_THE_SCENES_YOU_OWN", "SYNC_REGISTERS_THE_STUDIOS_YOU_OWN"],
    ["SYNC_SKIPPED_CANNOT_BE_REGISTERED", "SYNC_SITE_SKIPPED_CANNOT_BE_REGISTERED"],
    ["SYNC_NEEDS_A_COUNT_FIRST", "SYNC_SITE_NEEDS_A_COUNT_FIRST"],
    ["SYNC_NOTHING_LEFT_TO_SYNC", "SYNC_SITE_NOTHING_LEFT_TO_SYNC"],
    ["SYNC_DOWNLOADS_NOTHING", "SYNC_SITE_DOWNLOADS_NOTHING"],
    ["SYNC_OFFERS_ONE_SCENE", "SYNC_OFFERS_ONE_SITE"],
  ],

  /** Names what monitoring reaches, which is a scene whatever the run registers. */
  namesWhatMonitoringReaches: [
    "MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF",
    "SYNC_SITE_ALSO_MONITORS_THE_SCENES_ON_THEM",
  ],

  /** Names neither noun, so one declaration serves whatever the run registers. */
  namesNeitherNoun: [
    "SYNC_COUNT",
    "SYNC_NOT_YET_IN_WHISPARR",
    "SYNC_ALREADY_IN_WHISPARR",
    "SYNC_SKIPPED_NO_ID",
    "SYNC_COUNTING",
    "SYNC_NOTHING_COUNTED_YET",
    "SYNC_COUNT_DID_NOT_FINISH",
    "SYNC_IS_COUNTING",
    "SYNC_LIBRARY",
    "SYNC_ALSO_MONITOR",
    "SYNC_ALREADY_RUNNING",
    "SYNC_IS_STARTING",
    "SYNC_RUNS_IN_THE_JOB_DRAWER",
    "SYNC_ALSO_MONITORS_EACH",
    "SYNC_MONITORS_NOTHING",
  ],
};

const ACCOUNTED_FOR = [
  ...RENDERED_BY_THE_SYNC_SECTION.pairedByWhatTheRunRegisters.flat(),
  ...RENDERED_BY_THE_SYNC_SECTION.namesWhatMonitoringReaches,
  ...RENDERED_BY_THE_SYNC_SECTION.namesNeitherNoun,
];

function sentenceOf(name: string): string {
  const found = CONSTANTS.find(([declared]) => declared === name);
  if (found === undefined) throw new Error(`copy.ts declares no string constant ${name}`);
  return found[1];
}

describe("every sentence the sync section renders was walked for the noun it names", () => {
  it("accounts for each of them exactly once", () => {
    const declared = CONSTANTS.map(([name]) => name).filter((name) => name.startsWith("SYNC_"));

    expect([...declared].sort()).toEqual(
      [...ACCOUNTED_FOR.filter((name) => name.startsWith("SYNC_"))].sort(),
    );
    expect(new Set(ACCOUNTED_FOR).size).toBe(ACCOUNTED_FOR.length);
  });

  it("names both nouns in every pair, one apiece", () => {
    for (const [scene, studio] of RENDERED_BY_THE_SYNC_SECTION.pairedByWhatTheRunRegisters) {
      expect(contains(sentenceOf(scene), "scene"), `${scene} names no scene`).toBe(true);
      expect(contains(sentenceOf(scene), "studio"), `${scene} names a studio`).toBe(false);
      expect(contains(sentenceOf(studio), "studio"), `${studio} names no studio`).toBe(true);
      expect(contains(sentenceOf(studio), "scene"), `${studio} names a scene`).toBe(false);
    }
  });

  it("names a scene wherever it says what monitoring reaches", () => {
    for (const name of RENDERED_BY_THE_SYNC_SECTION.namesWhatMonitoringReaches) {
      expect(contains(sentenceOf(name), "scene"), `${name} names no scene`).toBe(true);
      expect(contains(sentenceOf(name), "studio"), `${name} says a studio is monitored`).toBe(
        false,
      );
    }
  });

  it("names neither noun in the sentences one declaration serves both ways", () => {
    for (const name of RENDERED_BY_THE_SYNC_SECTION.namesNeitherNoun) {
      expect(contains(sentenceOf(name), "scene"), `${name} names a scene`).toBe(false);
      expect(contains(sentenceOf(name), "studio"), `${name} names a studio`).toBe(false);
    }
  });
});
