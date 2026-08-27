/**
 * Round-trip + preservation contract for the options model. The save merge is reproduced as
 * `{ ...extras, ...options }` — the literal merge RenameSettingsPanel.saveOptions uses — so a pass
 * proves the SAME merge the panel runs.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import {
  NO_DESTINATION,
  normalizeOptions,
  extractUnmodeledFields,
  hasUnmigratedNameRules,
  hasUnmigratedDestinations,
  cloneDefaults,
  toStringKeyed,
  fromStringKeyed,
  DEFAULT_OPTIONS,
} from "./options";

// A blob with every modeled field at a value distinct from its default, in PascalCase (the wire
// spelling). The round-trip must return all of these unchanged.
function fullyPopulatedBlob() {
  return {
    FilenameTemplate: "$title",
    FolderTemplate: "$studio",
    FolderRoot: "D:/default",
    DateFormat: "yyyy",
    DurationFormat: "mm\\-ss",
    Performers: {
      Separator: " / ",
      MaxCount: 3,
      OnOverflow: "KeepFirst",
      Sort: "FavoriteFirst",
      WhitelistIds: [4],
      BlacklistIds: [5],
      IgnoreGenders: ["unknown"],
      GenderOrder: ["female", "male"],
    },
    Tags: {
      Separator: "-",
      MaxCount: 2,
      OnOverflow: "KeepFirst",
      Sort: "None",
      WhitelistIds: [21],
      BlacklistIds: [22],
      IgnoreGenders: [],
      GenderOrder: [],
    },
    IllegalReplacement: "_",
    SpaceReplacement: ".",
    RemoveCharacters: ",#",
    Case: "Lower",
    AsciiTransliterate: true,
    NormalizePunctuation: false,
    FilenameMax: 200,
    FullPathMax: 240,
    CrossVolumeConcurrency: 4,
    SameVolumeConcurrency: 16,
    DropOrder: ["tags", "studio"],
    OnlyOrganized: true,
    FilenameAsTitle: true,
    RequiredFields: ["title", "studio"],
    DuplicateSuffixFormat: "_{n}",
    AutoRenamerOnUpdate: true,
    StudioDestinations: {
      7: { Root: "D:/studios", Template: "seven" },
      12: { Root: "E:/studios", Template: "twelve" },
    },
    TagDestinations: {
      14: { Root: "D:/anime", Template: "" },
      15: { Root: "E:/docs", Template: "$year" },
    },
    PathDestinations: [
      { Pattern: "C:/in", Dest: { Root: "D:/out", Template: "" }, IsRegex: false },
      { Pattern: "^C:/re/.*$", Dest: { Root: "E:/out", Template: "$studio" }, IsRegex: true },
    ],
    ExcludeTagIds: [31],
    ExcludeStudioIds: [3, 9],
    ExcludePaths: [{ Pattern: "C:/skip", IsRegex: false }],
    AssociatedExtensions: ["srt", "vtt"],
    UnorganizedDestination: { Root: "D:/unorganized", Template: "$studio" },
    EnableStudioDestinations: true,
    EnableTagDestinations: true,
    EnableAdvancedRouting: true,
    RemoveEmptyFolder: true,
    SqueezeStudioNames: true,
    FieldReplacers: [{ TargetToken: "studio", Find: "'", Replace: "" }],
    StripLeadingArticles: true,
    Articles: ["The", "Le"],
    PreventTitlePerformer: true,
    PreventConsecutiveSegments: true,
  };
}

// FreeSpaceHeadroomBytes is the ONLY knob the panel never models, so it is the only one
// extractUnmodeledFields must still carry. The two concurrency knobs are now modeled (see below).
const UNMODELED_KNOB = { FreeSpaceHeadroomBytes: 2147483648 };

test("every modeled field survives load → no-op edit → save value-equal", () => {
  const blob = fullyPopulatedBlob();

  const loaded = normalizeOptions(blob);
  // Sanity: the load actually read the non-default values (not silently falling back to defaults).
  assert.equal(loaded.Performers.Sort, "FavoriteFirst");
  assert.equal(loaded.RemoveCharacters, ",#");
  assert.equal(loaded.FilenameAsTitle, true);
  assert.equal(loaded.RemoveEmptyFolder, true);
  assert.equal(loaded.EnableStudioDestinations, true);
  assert.equal(loaded.EnableTagDestinations, true);
  assert.equal(loaded.EnableAdvancedRouting, true);
  assert.deepEqual(loaded.AssociatedExtensions, ["srt", "vtt"]);
  assert.deepEqual(loaded.StudioDestinations, blob.StudioDestinations);
  assert.deepEqual(loaded.TagDestinations, blob.TagDestinations);
  assert.deepEqual(loaded.ExcludeTagIds, [31]);
  assert.deepEqual(loaded.Tags.WhitelistIds, [21]);
  assert.deepEqual(loaded.Performers.BlacklistIds, [5]);
  assert.deepEqual(loaded.PathDestinations, blob.PathDestinations);
  assert.equal(loaded.CrossVolumeConcurrency, 4);
  assert.equal(loaded.SameVolumeConcurrency, 16);

  // The panel's save merge, then a re-load (the next session reading what was persisted).
  const extras = extractUnmodeledFields(blob);
  const persisted = { ...extras, ...loaded };
  const reloaded = normalizeOptions(persisted);

  assert.deepEqual(reloaded, loaded);
});

test("cloneDefaults isolates every mutable collection from DEFAULT_OPTIONS", () => {
  const before = structuredClone(DEFAULT_OPTIONS);
  const clone = cloneDefaults();

  clone.StudioDestinations[1] = { Root: "x", Template: "" };
  clone.TagDestinations[2] = { Root: "x", Template: "" };
  clone.PathDestinations.push({
    Pattern: "p",
    Dest: { Root: "d", Template: "" },
    IsRegex: false,
  });
  clone.ExcludePaths.push({ Pattern: "p", IsRegex: false });
  clone.FieldReplacers.push({ TargetToken: "t", Find: "f", Replace: "r" });
  clone.ExcludeTagIds.push(77);
  clone.ExcludeStudioIds.push(99);
  clone.Articles.push("Der");
  clone.DropOrder.push("x");
  clone.RequiredFields.push("x");
  clone.Performers.IgnoreGenders.push("x");
  clone.Performers.GenderOrder.push("x");
  clone.Tags.IgnoreGenders.push("x");
  clone.Tags.GenderOrder.push("x");
  clone.Tags.WhitelistIds.push(88);

  assert.deepEqual(DEFAULT_OPTIONS, before);
});

test("FreeSpaceHeadroomBytes stays the only unmodeled knob; concurrency is modeled", () => {
  const blob = {
    ...fullyPopulatedBlob(),
    ...UNMODELED_KNOB,
    CrossVolumeConcurrency: 4,
    SameVolumeConcurrency: 16,
  };

  const extras = extractUnmodeledFields(blob);
  // Only FreeSpaceHeadroomBytes is carried as an extra; the two concurrency knobs are modeled now,
  // so extractUnmodeledFields must NOT carry them.
  assert.equal(extras.FreeSpaceHeadroomBytes, UNMODELED_KNOB.FreeSpaceHeadroomBytes);
  assert.ok(!("CrossVolumeConcurrency" in extras));
  assert.ok(!("SameVolumeConcurrency" in extras));

  // Annotated because a spread of an index-signature type into a concrete one keeps only the
  // concrete keys, which would leave the carried-through extra unreadable here — the very thing
  // under test.
  const persisted: Record<string, unknown> = { ...extras, ...normalizeOptions(blob) };
  assert.equal(persisted.FreeSpaceHeadroomBytes, UNMODELED_KNOB.FreeSpaceHeadroomBytes);
  assert.equal(persisted.CrossVolumeConcurrency, 4);
  assert.equal(persisted.SameVolumeConcurrency, 16);

  // A second load → save keeps them: the merge re-extracts and re-merges with no drift.
  const extras2 = extractUnmodeledFields(persisted);
  assert.ok(!("CrossVolumeConcurrency" in extras2));
  assert.ok(!("SameVolumeConcurrency" in extras2));
  const persisted2: Record<string, unknown> = { ...extras2, ...normalizeOptions(persisted) };
  assert.equal(persisted2.FreeSpaceHeadroomBytes, UNMODELED_KNOB.FreeSpaceHeadroomBytes);
  assert.equal(persisted2.CrossVolumeConcurrency, 4);
  assert.equal(persisted2.SameVolumeConcurrency, 16);
});

test("a concurrency value stored before it was modeled still loads (not the 2/8 defaults)", () => {
  // These keys used to be UNMODELED (carried by extractUnmodeledFields). A blob saved back then can
  // hold a hand-tuned value; now that the fields are modeled, normalizeOptions must read that stored
  // value rather than reverting it to the 2/8 defaults, and the save merge must not drift it.
  const preExposureBlob = { CrossVolumeConcurrency: 4, SameVolumeConcurrency: 16 };

  const loaded = normalizeOptions(preExposureBlob);
  assert.equal(loaded.CrossVolumeConcurrency, 4);
  assert.equal(loaded.SameVolumeConcurrency, 16);

  const persisted = { ...extractUnmodeledFields(preExposureBlob), ...loaded };
  const reloaded = normalizeOptions(persisted);
  assert.equal(reloaded.CrossVolumeConcurrency, 4);
  assert.equal(reloaded.SameVolumeConcurrency, 16);
});

test("a blob absent both concurrency keys normalizes them to the 2/8 defaults", () => {
  const loaded = normalizeOptions({ FilenameTemplate: "$title" });
  assert.equal(loaded.CrossVolumeConcurrency, 2);
  assert.equal(loaded.SameVolumeConcurrency, 8);
});

test("a stored blob with the old defaults survives load → save unchanged", () => {
  // A blob saved before the default flip carries the OLD template + both flags off. The new defaults
  // must NOT overwrite a present stored value — normalizeOptions falls back to a default only when a
  // field is ABSENT — so an existing user's saved options never silently change.
  const oldBlob = {
    FilenameTemplate: "$title{ [$resolution]}",
    PreventConsecutiveSegments: false,
    FilenameAsTitle: false,
  };

  const loaded = normalizeOptions(oldBlob);
  assert.equal(loaded.FilenameTemplate, "$title{ [$resolution]}");
  assert.equal(loaded.PreventConsecutiveSegments, false);
  assert.equal(loaded.FilenameAsTitle, false);

  // The panel's save merge, then a re-load (the next session reading what was persisted): the three
  // old values must still survive rather than reverting to the new defaults.
  const persisted = { ...extractUnmodeledFields(oldBlob), ...loaded };
  const reloaded = normalizeOptions(persisted);
  assert.equal(reloaded.FilenameTemplate, "$title{ [$resolution]}");
  assert.equal(reloaded.PreventConsecutiveSegments, false);
  assert.equal(reloaded.FilenameAsTitle, false);
});

test("a blob predating the three gate flags normalizes them to false", () => {
  // A blob saved before this phase has no EnableStudioDestinations/EnableTagDestinations/
  // EnableAdvancedRouting keys at all. Their absence must fall back to the DEFAULT_OPTIONS false,
  // not error and not spuriously turn a gate on.
  const oldBlob = {
    StudioDestinations: { 7: { Root: "D:/studios", Template: "seven" } },
  };

  const loaded = normalizeOptions(oldBlob);
  assert.equal(loaded.EnableStudioDestinations, false);
  assert.equal(loaded.EnableTagDestinations, false);
  assert.equal(loaded.EnableAdvancedRouting, false);
});

test("a stale camelCase duplicate key is dropped by normalizeOptions", () => {
  const blob = {
    StudioDestinations: { 7: { Root: "D:/canonical", Template: "" } },
    studioDestinations: { 7: { Root: "D:/stale", Template: "" } },
  };

  const normalized = normalizeOptions(blob);
  assert.deepEqual(normalized.StudioDestinations, {
    7: { Root: "D:/canonical", Template: "" },
  });
  assert.ok(!("studioDestinations" in normalized));
});

test("a number-keyed destination map becomes a string-keyed map preserving values", () => {
  const map = { 3: { Root: "/a", Template: "" }, 12: { Root: "/b", Template: "x" } };
  assert.deepEqual(toStringKeyed(map), map);
});

test("a round-trip through the editor's string keys restores number keys identically", () => {
  const original = { 3: { Root: "/a", Template: "" }, 12: { Root: "/b", Template: "x" } };
  assert.deepEqual(fromStringKeyed(toStringKeyed(original)), original);
});

test("a non-integer editor key is dropped rather than producing a NaN key", () => {
  const dest = { Root: "/a", Template: "" };
  assert.deepEqual(fromStringKeyed({ x: dest, "1.5": dest, "9": dest }), { 9: dest });
});

test("a stored destination that is still a bare string loads as the moves-nothing one", () => {
  // The shape a blob written before destinations became objects holds. It cannot be placed without
  // Cove's library paths, which is the backend conversion's decision to make, so the panel shows
  // the destination that moves nothing rather than guessing at a root.
  const loaded = normalizeOptions({ TagDestinations: { 14: "D:/anime" } });

  assert.deepEqual(loaded.TagDestinations, { 14: NO_DESTINATION });
});

test("an absent unorganized destination stays absent, not a destination naming nothing", () => {
  // Only the absent one falls through to the only-organized gate, so the two are not the same
  // setting and a load must not turn one into the other.
  assert.equal(normalizeOptions({}).UnorganizedDestination, null);
  assert.deepEqual(
    normalizeOptions({ UnorganizedDestination: { Root: "", Template: "" } }).UnorganizedDestination,
    NO_DESTINATION,
  );
});

test("every destination-map key a save persists parses as an integer", () => {
  // The backend binds both maps as `Dictionary<int, Destination>` and answers a bind failure with
  // DEFAULTS, so one unparseable key silently discards the user's whole settings blob. JSON object
  // keys are strings, which is why this is asserted on what a save actually writes.
  const persisted: Record<string, unknown> = {
    ...extractUnmodeledFields(fullyPopulatedBlob()),
    ...normalizeOptions(fullyPopulatedBlob()),
  };

  for (const field of ["StudioDestinations", "TagDestinations"]) {
    const map = persisted[field] as Record<string, string>;
    assert.ok(Object.keys(map).length > 0, `${field} must be populated for this to prove anything`);
    for (const key of Object.keys(map)) {
      assert.ok(/^-?\d+$/.test(key), `${field} key "${key}" would fail the backend's int bind`);
    }
  }
});

test("a name-keyed destination map coerces to empty rather than persisting an unparseable key", () => {
  // A blob written before the rules keyed on ids holds names here. The backend's one-time conversion
  // is what recovers them; this panel must not carry a name back into a save.
  const loaded = normalizeOptions({
    TagDestinations: { Anime: { Root: "D:/anime", Template: "" } },
    ExcludeTagIds: ["nsfw"],
  });

  assert.deepEqual(loaded.TagDestinations, {});
  assert.deepEqual(loaded.ExcludeTagIds, []);
});

test("a name-valued whitelist coerces to empty rather than reaching the backend as a string", () => {
  const loaded = normalizeOptions({
    Tags: { WhitelistIds: ["anime", 21] },
    Performers: { BlacklistIds: ["someone"] },
  });

  assert.deepEqual(loaded.Tags.WhitelistIds, [21]);
  assert.deepEqual(loaded.Performers.BlacklistIds, []);
});

// ── hasUnmigratedNameRules: what stops the erasure above from reaching the store ──
//
// The two tests above pin that normalizeOptions empties every name-keyed rule.
// `extractUnmodeledFields` walks TOP-LEVEL keys only, and both groups and `TagDestinations` are
// modeled, so the emptied shapes are what a save would persist — over rules nothing else keeps a copy
// of. This predicate is what the panel refuses to save on, so it must be true for exactly the blobs
// where that loss is real.

test("a blob still holding names is reported as awaiting the backend conversion", () => {
  for (const legacy of [
    { Performers: { Whitelist: ["Jane Doe"] } },
    { Performers: { Blacklist: ["John Roe"] } },
    { Tags: { Whitelist: ["anime"] } },
    { Tags: { Blacklist: ["spam"] } },
    { TagDestinations: { Anime: "D:/anime" } },
    { ExcludeTags: ["nsfw"] },
    // Key casing is forgiving on the backend's own scan, so a hand-edited spelling must not read as
    // "nothing pending" here and unblock a save the backend has not converted for.
    { tags: { whitelist: ["anime"] } },
  ]) {
    assert.equal(hasUnmigratedNameRules(legacy), true, JSON.stringify(legacy));
  }
});

test("the empty legacy keys every pre-migration install stored are not read as pending", () => {
  // The shape the shipped panel wrote for a user who configured neither group: each legacy key
  // present, none holding a name. The backend has nothing to resolve in it, and it stamps itself done
  // on that no-work path — so blocking Save here would lock the panel permanently.
  const realistic = {
    FilenameTemplate: "$title",
    Performers: { Separator: " ", Whitelist: [], Blacklist: [] },
    Tags: { Separator: " ", Whitelist: [], Blacklist: [] },
    TagDestinations: {},
    ExcludeTags: [],
  };

  assert.equal(hasUnmigratedNameRules(realistic), false);
  // A blank entry is not a name either: the backend's scan skips one, so it never becomes work.
  assert.equal(hasUnmigratedNameRules({ Tags: { Whitelist: ["", "   "] } }), false);
  assert.equal(hasUnmigratedNameRules({ ExcludeTags: ["", 7, null] }), false);
});

test("a converted blob, the defaults and a non-object are all reported as nothing pending", () => {
  const converted = {
    Performers: { WhitelistIds: [11], BlacklistIds: [22] },
    Tags: { WhitelistIds: [33], BlacklistIds: [44] },
    TagDestinations: { 9: "D:/anime", 0: "D:/zero" },
    ExcludeTagIds: [55],
  };

  assert.equal(hasUnmigratedNameRules(converted), false);
  assert.equal(hasUnmigratedNameRules(cloneDefaults()), false);
  assert.equal(hasUnmigratedNameRules(null), false);
  assert.equal(hasUnmigratedNameRules("not an object"), false);
});

test("a destination key counts as a name exactly when the backend's int parse rejects it", () => {
  // Transcribed by hand from `int.TryParse(key, NumberStyles.Integer, InvariantCulture)`, which is
  // what OptionsMigration.Scan asks about each key; the same table is pinned against the real parser
  // in OptionsMigrationScanTests, so a C# change breaks a test that names this one. Why the two must
  // agree exactly is on `isIdKey`.
  const keySpellings: [string, boolean][] = [
    ["9", false],
    ["0", false],
    ["2147483647", false],
    ["-9", false],
    ["+9", false],
    // Leading zeroes and surrounding ASCII whitespace all parse as an int, so none of them is a name.
    ["09", false],
    [" 9 ", false],
    ["\t9\n", false],
    ["1e3", true],
    ["1.5", true],
    // NumberStyles.Integer permits no group separator.
    ["9,9", true],
    ["2147483648", true],
    ["-2147483649", true],
    // A non-breaking space is whitespace to JavaScript and not to the int parse, so it is a name.
    ["\u00a09", true],
    ["", true],
    ["Anime", true],
  ];
  for (const [key, pending] of keySpellings) {
    assert.equal(
      hasUnmigratedNameRules({ TagDestinations: { [key]: "D:/x" } }),
      pending,
      `TagDestinations key ${JSON.stringify(key)}`,
    );
  }
});

test("only the tag map is scanned for name keys, matching which rules the conversion rewrites", () => {
  // Studio and path rules were never name-keyed, so the conversion does not touch them and a
  // non-integer key there is not something a host start will ever resolve.
  assert.equal(hasUnmigratedNameRules({ StudioDestinations: { Vixen: "D:/v" } }), false);
});

// -- hasUnmigratedDestinations: the same refusal for the destination half of that conversion --
//
// A destination stored before a destination became a root plus a template is a bare absolute path.
// `normalizeOptions` reads one as
// NO_DESTINATION (pinned above, "a stored destination that is still a bare string loads as the
// moves-nothing one"), and `UnorganizedDestination` as no route at all, so a save carries those blanks
// into the store over folders nothing else holds a copy of. The conversion that would rewrite them
// needs at least one Cove library path to choose a root from and defers while there is none, so the
// old shape survives restarts.

test("a stored destination counts as unconverted exactly when the backend finds a site to rewrite", () => {
  // Transcribed by hand from `OptionsMigration.ConvertDestinationsToRoots`, whose site walk takes a
  // JSON STRING and nothing else; the same table is pinned against the real converter in
  // OptionsMigrationDestinationTests, so a C# change breaks a test that names this one.
  const valueKinds: [unknown, boolean][] = [
    ["D:/library/videos", true],
    // The empty string is a site too: the conversion rewrites it to the global folder template under
    // the file's own library path, which is not what this panel reads it as.
    ["", true],
    [{ Root: "D:/library", Template: "videos" }, false],
    [null, false],
    [7, false],
    [true, false],
    [["D:/library/videos"], false],
  ];

  for (const [value, unconverted] of valueKinds) {
    assert.equal(
      hasUnmigratedDestinations({ StudioDestinations: { 101: value } }),
      unconverted,
      `StudioDestinations value ${JSON.stringify(value)}`,
    );
  }
});

test("every place the conversion rewrites a destination is a place this predicate reads", () => {
  for (const blob of [
    { StudioDestinations: { 101: "D:/a" } },
    { TagDestinations: { 7: "D:/a" } },
    { PathDestinations: [{ Pattern: "D:/in", Dest: "D:/a", IsRegex: false }] },
    { UnorganizedDestination: "D:/a" },
    // Key casing is forgiving on the backend's own walk, so a hand-edited spelling must not read as
    // converted here and unblock a save the backend has not converted for.
    { studiodestinations: { 101: "D:/a" } },
    { pathDestinations: [{ pattern: "D:/in", dest: "D:/a" }] },
    { unorganizeddestination: "D:/a" },
  ]) {
    assert.equal(hasUnmigratedDestinations(blob), true, JSON.stringify(blob));
  }
});

test("the global folder template is a string the conversion leaves alone, so it is not a site", () => {
  // It is the one destination the conversion deliberately keeps as stored, because FolderRoot's
  // default already means what a relative template was always measured from. Reading it as a site
  // would refuse a save on every install that set a folder template, and permanently: the conversion
  // stamps the blob done once it finds nothing to rewrite.
  assert.equal(
    hasUnmigratedDestinations({ FolderTemplate: "$studio", FolderRoot: "D:/library" }),
    false,
  );
});

test("a converted destination, the defaults and a non-object are all reported as converted", () => {
  const converted = {
    FolderTemplate: "$studio",
    StudioDestinations: { 101: { Root: "D:/library", Template: "videos/$studio" } },
    PathDestinations: [
      { Pattern: "D:/in", Dest: { Root: "D:/library", Template: "sorted" }, IsRegex: false },
    ],
    UnorganizedDestination: { Root: "D:/library", Template: "unsorted" },
  };

  assert.equal(hasUnmigratedDestinations(converted), false);
  assert.equal(hasUnmigratedDestinations({ StudioDestinations: {}, PathDestinations: [] }), false);
  assert.equal(hasUnmigratedDestinations(cloneDefaults()), false);
  assert.equal(hasUnmigratedDestinations(null), false);
  assert.equal(hasUnmigratedDestinations("not an object"), false);
  // A JSON array root is not a blob the backend's own parse accepts either.
  assert.equal(hasUnmigratedDestinations([{ StudioDestinations: { 101: "D:/a" } }]), false);
});

test("what a save persists never reads as unconverted, so one save cannot lock out the next", () => {
  const persisted: Record<string, unknown> = {
    ...extractUnmodeledFields(fullyPopulatedBlob()),
    ...normalizeOptions(fullyPopulatedBlob()),
  };

  assert.equal(hasUnmigratedDestinations(persisted), false);
});
