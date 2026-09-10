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

/**
 * The catalogue tab's own sentences: why the whole grid cannot answer, what an empty grid means,
 * what the count beside it counts, the one card-level answer that is not a failure, and what a
 * facet menu's search box offers and reads at each answer a value lookup can give.
 */
const RENDERED_BY_THE_MISSING_TAB = [
  "MISSING_TAB_HEADING",
  "FACET_MENU_SEARCH",
  "FACET_MENU_NO_MATCHES",
  "FACET_VALUES_ASKING",
  "FACET_VALUES_NONE_MATCH",
  "FACET_VALUES_NOT_READ",
  "NO_METADATA_PROVIDER_CONFIGURED",
  "NO_PROVIDER_ID_FOR_ENTITY",
  "NO_TITLES_MATCH",
  "NO_SCENES_MATCH_THESE_FILTERS",
  "NO_SCENES_WITHOUT_SUB_STUDIOS",
  "EVERY_SCENE_ON_THIS_PAGE_IS_OWNED",
  "ACTION_REFRESH",
  "WHISPARR_STATUS_NOT_READ",
  "WHISPARR_KEEPS_NO_SCENE_RECORDS",
  "COUNT_IS_THE_CATALOGUE_SIZE",
  "SEARCH_WITH_NO_ENTRY",
  "THE_METADATA_SOURCE",
  "BULK_REPORTS_IN_THE_JOB_DRAWER",
];

/** Sentences the connect surface reads through its own kind table. */
const RENDERED_BY_THE_CONNECT_SURFACE = ["CONNECT_NOT_CONFIGURED", "CONNECT_KEY_REJECTED"];

/** Sentences the import-behaviour section reads: one per upgrade behaviour. */
const RENDERED_BY_THE_IMPORT_BEHAVIOR_SECTION = [
  "UPGRADE_KEEPS_BOTH_FILES",
  "UPGRADE_DROPS_THE_SUPERSEDED_FILE",
];

/**
 * The sentence both read surfaces show when a refresh failed over content already on screen.
 *
 * Its own group rather than either surface's, because two surfaces render it and a per-surface list
 * would have to name it twice.
 */
const RENDERED_ON_A_STALE_READ = ["READ_IS_STALE"];

/** Sentences the import banner reads: its heading, and one per refusal cause. */
const RENDERED_BY_THE_IMPORT_BANNER = [
  "IMPORTS_UNREADABLE",
  "IMPORT_CAUSE_NOT_FOUND",
  "IMPORT_CAUSE_AMBIGUOUS",
  "IMPORT_CAUSE_UNREADABLE",
];

/** The entity control's two names. It carries the product's mark instead of a word, so its
 * accessible name is the only name it has. */
const RENDERED_BY_THE_ENTITY_CONTROL = [
  "WHISPARR_NOT_MONITORED",
  "WHISPARR_MONITORED",
  "MONITORING_COULD_NOT_BE_READ",
  "ACTION_DID_NOT_REACH_WHISPARR",
  "ACTION_ABSENT_IN_THIS_VERSION",
];

/**
 * The library toolbar control's two names, and every reason it states for the whole page.
 *
 * Each reason rides the control rather than the cards, because a page of cards would state it once
 * per card. Four of them are read through the page's own reason table; the last is a fact about the
 * display mode, which no read reports.
 */
const RENDERED_BY_THE_LIBRARY_PILL = [
  "SHOW_WHISPARR_STATUS",
  "HIDE_WHISPARR_STATUS",
  "NO_WHISPARR_CONNECTED",
  "WHISPARR_KEEPS_NO_RECORD_OF_THESE",
  "WHISPARR_STATUS_COULD_NOT_BE_READ",
  "THE_STATUS_READ_DID_NOT_COMPLETE",
  "NO_PLACE_FOR_A_CARD_STATUS_HERE",
];

/**
 * The name of every row the monitor menu offers. Each is placed on a menu item by
 * `monitoring/monitorMenuLogic.ts`; the menu that draws those items arrives with the rest of the
 * entity surface. A row draws its name and its glyph, and states nothing beneath itself.
 */
const CARRIED_BY_THE_MONITOR_MENU_ITEMS = [
  "MENU_MONITOR",
  "SCOPE_FUTURE_SCENES",
  "SCOPE_ALL_SCENES",
  "MENU_UNMONITOR",
  "ACTION_ADD_ALL_MISSING",
  "ACTION_REFLECT_OWNED",
  "ACTION_SEARCH_ALL_MONITORED",
];

/**
 * The two consequences the confirmation states before the wider scope is carried out. Its own group,
 * because the confirmation is the one surface that states either of them.
 */
const RENDERED_BY_THE_ALL_SCENES_CONFIRMATION = [
  "ALL_SCENES_MARKS_THE_BACK_CATALOGUE",
  "ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE",
];

/**
 * The consequence the confirmation states before the search is carried out. Its own group, because
 * that confirmation is the one surface that states it.
 */
const RENDERED_BY_THE_SEARCH_CONFIRMATION = ["SEARCH_ALL_MONITORED_SPENDS_TRAFFIC_AND_DISK"];

/**
 * The sync section's own sentences: the count control's first-press name, the three count rows, what
 * the skipped row means and what to do about it, and the section's reading, empty, failed and busy
 * lines.
 *
 * The control's second name is the shared refresh verb, which another group already accounts for.
 */
const RENDERED_BY_THE_SYNC_SECTION = [
  "SYNC_COUNT",
  "SYNC_NOT_YET_IN_WHISPARR",
  "SYNC_ALREADY_IN_WHISPARR",
  "SYNC_SKIPPED_NO_ID",
  "SYNC_SKIPPED_CANNOT_BE_REGISTERED",
  "SYNC_COUNTING",
  "SYNC_NOTHING_COUNTED_YET",
  "SYNC_COUNT_DID_NOT_FINISH",
  "SYNC_IS_COUNTING",
];

/**
 * The consequence the confirmation states before a whole catalogue is marked. Its own group, because
 * that confirmation is the one surface that states it.
 */
const RENDERED_BY_THE_MONITOR_ALL_CONFIRMATION = ["MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF"];

/**
 * The selection overlay's own sentences: why it sometimes has nothing to offer, how a refused
 * gesture is stated, and its two ways out.
 */
const RENDERED_BY_THE_BULK_OVERLAY = [
  "BULK_ACTIONS_COULD_NOT_BE_OFFERED",
  "BULK_SELECTION_IS_OVER_THE_BOUND",
  "BULK_SELECTION_WAS_NOT_STARTED",
  "BULK_CANCEL",
  "BULK_CLOSE",
];

/**
 * The scene tab's own labels and sentences: the word in its header, its three fact labels, the name
 * of each of its four controls, the reason each control gives where it cannot act, and the one
 * sentence that confirms a search.
 *
 * The reasons it states for a refused READ are sentences other surfaces already declare, so those
 * are not named here.
 */
const RENDERED_BY_THE_SCENE_TAB = [
  "SCENE_HEADER_WHISPARR",
  "SCENE_FACT_QUALITY",
  "SCENE_FACT_PROFILE",
  "SCENE_FACT_CUTOFF",
  "SCENE_ADD",
  "SCENE_SEARCH",
  "MONITOR_IN_WHISPARR",
  "STOP_MONITORING_IN_WHISPARR",
  "SCENE_EXCLUDE",
  "SCENE_REMOVE_EXCLUSION",
  "SCENE_SEARCH_NEEDS_AN_ENTRY",
  "SCENE_MONITOR_NEEDS_AN_ENTRY",
  "SCENE_SEARCH_NEEDS_MONITORING",
  "SCENE_IS_ALREADY_IN_WHISPARR",
  "SCENE_IS_ON_THE_EXCLUSION_LIST",
  "SCENE_SEARCH_IS_WITH_WHISPARR",
];

/**
 * The sentence a selection over the search row's own bound is refused with, and the two row names
 * this overlay is the only surface to render.
 *
 * Its other three rows read a name named in another group: the monitor menu declares the pair, and
 * the search row's name is the scene tab's.
 */
const RENDERED_BY_THE_BATCH_OVERLAY = [
  "BATCH_SEARCH_IS_OVER_THE_BOUND",
  "MENU_ADD",
  "MENU_EXCLUDE",
];

/**
 * One sentence per reason a monitor control can be unavailable. The menu rules module maps the kind
 * the server answered onto exactly one of these, and a kind with none would be a dimmed control with
 * nothing to hear.
 */
const RENDERED_AS_A_MONITOR_REFUSAL = [
  "WAITING_FOR_WHISPARR",
  "NO_INSTANCE_CONNECTED",
  "NO_IDENTITY_IN_THIS_NAMESPACE",
  "SEVERAL_IDENTITIES_IN_THIS_NAMESPACE",
  "INSTANCE_OFFERS_NO_QUALITY_PROFILE",
  "INSTANCE_OFFERS_NO_ROOT_FOLDER",
  "INSTANCE_REFUSED",
  "INSTANCE_ANSWER_WAS_TOO_LARGE_TO_READ",
  "INSTANCE_HOLDS_NO_SUCH_ENTRY",
  "INSTANCE_DID_NOT_REPORT_THE_CHANGE",
];

/** The outcome sentence for the one secondary action that can decline to do anything. */
const RENDERED_WHEN_REFLECT_OWNED_IS_SKIPPED = [
  "REFLECT_OWNED_SKIPPED",
  "REFLECT_OWNED_SKIPPED_SETTING_UNREADABLE",
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
    const accountedByName = [
      ...CARRIED_BY_THE_MONITOR_MENU_ITEMS,
      ...RENDERED_BY_THE_BULK_OVERLAY,
      ...RENDERED_BY_THE_BATCH_OVERLAY,
      ...RENDERED_BY_THE_MISSING_TAB,
      ...RENDERED_BY_THE_SCENE_TAB,
      ...RENDERED_BY_THE_CONNECT_SURFACE,
      ...RENDERED_BY_THE_ENTITY_CONTROL,
      ...RENDERED_BY_THE_LIBRARY_PILL,
      ...RENDERED_BY_THE_IMPORT_BANNER,
      ...RENDERED_BY_THE_IMPORT_BEHAVIOR_SECTION,
      ...RENDERED_AS_A_MONITOR_REFUSAL,
      ...RENDERED_ON_A_STALE_READ,
      ...RENDERED_WHEN_REFLECT_OWNED_IS_SKIPPED,
      ...RENDERED_BY_THE_ALL_SCENES_CONFIRMATION,
      ...RENDERED_BY_THE_SEARCH_CONFIRMATION,
      ...RENDERED_BY_THE_MONITOR_ALL_CONFIRMATION,
      ...RENDERED_BY_THE_SYNC_SECTION,
    ];

    const orphans = CONSTANTS.filter(
      ([name, sentence]) => !fromAKind.has(sentence) && !accountedByName.includes(name),
    ).map(([name]) => name);

    expect(orphans).toEqual([]);
  });

  it("names no sentence that no longer exists", () => {
    const declared = CONSTANTS.map(([name]) => name);
    for (const name of [
      ...CARRIED_BY_THE_MONITOR_MENU_ITEMS,
      ...RENDERED_BY_THE_BULK_OVERLAY,
      ...RENDERED_BY_THE_BATCH_OVERLAY,
      ...RENDERED_BY_THE_MISSING_TAB,
      ...RENDERED_BY_THE_SCENE_TAB,
      ...RENDERED_BY_THE_CONNECT_SURFACE,
      ...RENDERED_BY_THE_ENTITY_CONTROL,
      ...RENDERED_BY_THE_LIBRARY_PILL,
      ...RENDERED_BY_THE_IMPORT_BANNER,
      ...RENDERED_BY_THE_IMPORT_BEHAVIOR_SECTION,
      ...RENDERED_AS_A_MONITOR_REFUSAL,
      ...RENDERED_ON_A_STALE_READ,
      ...RENDERED_WHEN_REFLECT_OWNED_IS_SKIPPED,
      ...RENDERED_BY_THE_ALL_SCENES_CONFIRMATION,
      ...RENDERED_BY_THE_SEARCH_CONFIRMATION,
      ...RENDERED_BY_THE_MONITOR_ALL_CONFIRMATION,
      ...RENDERED_BY_THE_SYNC_SECTION,
    ]) {
      expect(declared, name).toContain(name);
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
