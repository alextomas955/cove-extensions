/**
 * Round-trip + preservation contract for the options model. The save merge is reproduced as
 * `{ ...extras, ...options }` — the literal merge RenameSettingsPanel.saveOptions uses — so a pass
 * proves the SAME merge the panel runs.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import {
  normalizeOptions,
  extractUnmodeledFields,
  hasUnmigratedNameRules,
  cloneDefaults,
  toStringKeyed,
  fromStringKeyed,
  newDestination,
  defaultDestinationRoot,
  chosenLibraryPath,
  DEFAULT_OPTIONS,
  type RenamerOptions,
} from "./options";

// A blob with every modeled field at a value distinct from its default, in PascalCase (the wire
// spelling). The round-trip must return all of these unchanged.
function fullyPopulatedBlob() {
  return {
    FilenameTemplate: "$title",
    FolderTemplate: "$studio",
    DateFormat: "yyyy",
    DurationFormat: "mm\\-ss",
    Performers: {
      Separator: " / ",
      MaxCount: 3,
      OnOverflow: "KeepFirst",
      Sort: "FavoriteFirst",
      WhitelistIds: [11],
      BlacklistIds: [22],
      IgnoreGenders: ["unknown"],
      GenderOrder: ["female", "male"],
    },
    Tags: {
      Separator: "-",
      MaxCount: 2,
      OnOverflow: "KeepFirst",
      Sort: "None",
      WhitelistIds: [33],
      BlacklistIds: [44],
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
      7: { Root: "D:/lib", Template: "studios/seven" },
      12: { Root: "E:/lib", Template: "studios/twelve" },
    },
    TagDestinations: {
      5: { Root: "D:/lib", Template: "anime" },
      6: { Root: "E:/lib", Template: "docs" },
    },
    PathDestinations: [
      { Pattern: "C:/in", Dest: { Root: "D:/lib", Template: "out" }, IsRegex: false },
      { Pattern: "^C:/re/.*$", Dest: { Root: "E:/lib", Template: "out" }, IsRegex: true },
    ],
    ExcludeTagIds: [77],
    ExcludeStudioIds: [3, 9],
    ExcludePaths: [{ Pattern: "C:/skip", IsRegex: false }],
    AllowedRoots: ["D:/", "E:/"],
    AssociatedExtensions: ["srt", "vtt"],
    UnorganizedDestination: { Root: "D:/lib", Template: "unorganized" },
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
  assert.deepEqual(loaded.StudioDestinations, {
    7: { Root: "D:/lib", Template: "studios/seven" },
    12: { Root: "E:/lib", Template: "studios/twelve" },
  });
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
  const before = JSON.parse(JSON.stringify(DEFAULT_OPTIONS)) as RenamerOptions;
  const clone = cloneDefaults();

  // Written as string keys because that is what the derived index signature accepts; JS object keys
  // are strings either way, so this sets the same entries the panel's numeric ids do.
  clone.StudioDestinations["1"] = { Root: "/lib", Template: "x" };
  clone.TagDestinations["2"] = { Root: "/lib", Template: "x" };
  clone.PathDestinations.push({
    Pattern: "p",
    Dest: { Root: "/lib", Template: "d" },
    IsRegex: false,
  });
  clone.ExcludePaths.push({ Pattern: "p", IsRegex: false });
  clone.FieldReplacers.push({ TargetToken: "t", Find: "f", Replace: "r" });
  clone.ExcludeTagIds.push(88);
  clone.ExcludeStudioIds.push(99);
  clone.AllowedRoots.push("Z:/");
  clone.Articles.push("Der");
  clone.DropOrder.push("x");
  clone.RequiredFields.push("x");
  clone.Performers.IgnoreGenders.push("x");
  clone.Performers.GenderOrder.push("x");
  clone.Tags.IgnoreGenders.push("x");
  clone.Tags.GenderOrder.push("x");
  clone.Tags.WhitelistIds.push(1);
  clone.Tags.BlacklistIds.push(2);
  clone.Performers.WhitelistIds.push(3);
  clone.Performers.BlacklistIds.push(4);

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

  // Typed as the loose blob it is: the merge's whole point is that it carries keys the model does not
  // declare, which is exactly what a RenamerOptions-shaped type would refuse to describe.
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
  // A blob saved before a default flip must NOT be overwritten by the new default — normalizeOptions
  // falls back to a default only when a field is ABSENT — so an existing user's saved options never
  // silently change.
  //
  // Each stored value below is the OPPOSITE of what this build defaults to, which is the only way the
  // assertion can fail if preservation breaks: FilenameAsTitle is stored TRUE against a false default
  // precisely because it used to be stored false against a true one, and reading a stored value that
  // matches the default proves nothing about whether it was read at all.
  const oldBlob = {
    FilenameTemplate: "$title{ [$resolution]}",
    PreventConsecutiveSegments: false,
    FilenameAsTitle: true,
  };

  const loaded = normalizeOptions(oldBlob);
  assert.equal(loaded.FilenameTemplate, "$title{ [$resolution]}");
  assert.equal(loaded.PreventConsecutiveSegments, false);
  assert.equal(loaded.FilenameAsTitle, true);

  // The panel's save merge, then a re-load (the next session reading what was persisted): the three
  // old values must still survive rather than reverting to the new defaults.
  const persisted = { ...extractUnmodeledFields(oldBlob), ...loaded };
  const reloaded = normalizeOptions(persisted);
  assert.equal(reloaded.FilenameTemplate, "$title{ [$resolution]}");
  assert.equal(reloaded.PreventConsecutiveSegments, false);
  assert.equal(reloaded.FilenameAsTitle, true);
});

test("a blob predating the three gate flags normalizes them to false", () => {
  // A blob saved before this phase has no EnableStudioDestinations/EnableTagDestinations/
  // EnableAdvancedRouting keys at all. Their absence must fall back to the DEFAULT_OPTIONS false,
  // not error and not spuriously turn a gate on.
  const oldBlob = {
    StudioDestinations: { 7: { Root: "D:/lib", Template: "studios/seven" } },
  };

  const loaded = normalizeOptions(oldBlob);
  assert.equal(loaded.EnableStudioDestinations, false);
  assert.equal(loaded.EnableTagDestinations, false);
  assert.equal(loaded.EnableAdvancedRouting, false);
});

test("a stale camelCase duplicate key is dropped by normalizeOptions", () => {
  const blob = {
    StudioDestinations: { 7: { Root: "D:/lib", Template: "canonical" } },
    studioDestinations: { 7: { Root: "D:/lib", Template: "stale" } },
  };

  const normalized = normalizeOptions(blob);
  assert.deepEqual(normalized.StudioDestinations, {
    7: { Root: "D:/lib", Template: "canonical" },
  });
  assert.ok(!("studioDestinations" in normalized));
});

// ── The id-valued wire contract ───────────────────────────────────────────────────────────────────
//
// The names below are transcribed BY HAND from `src/Renamer/Options/RenamerOptions.cs` — the
// `MultiValueOptions.WhitelistIds` / `.BlacklistIds` properties and the `RenamerOptions.TagDestinations`
// / `.ExcludeTagIds` properties — and are deliberately NOT derived from options.ts. An expectation
// computed from the module it checks agrees with itself forever and can never detect drift.
//
// What drift costs here: the panel writes this blob verbatim (JSON.stringify of the options object)
// and the backend reads it with `PropertyNameCaseInsensitive`, so a name that does not match is not an
// error on either side — the property is simply unbound and the field silently takes its default. A
// misspelling therefore type-checks, deserializes, and reverts the user's configuration at run time.
//
// Casing: PascalCase, matching the C# property spelling, because `RenamerOptions.JsonOptions` applies
// no naming policy to this blob. That is a different convention from the host's own API DTOs.
const CSHARP_MULTI_VALUE_ID_FIELDS = ["WhitelistIds", "BlacklistIds"];
const CSHARP_TAG_DESTINATIONS = "TagDestinations";
const CSHARP_EXCLUDE_TAG_IDS = "ExcludeTagIds";
// The pre-migration spellings the C# record no longer declares. Emitting one is the failure above.
const RETIRED_MULTI_VALUE_FIELDS = ["Whitelist", "Blacklist"];
const RETIRED_EXCLUDE_TAGS = "ExcludeTags";
// The two entity groups that carry the migrated id lists. Named as their literal keys rather than
// plain strings so the loops below index the options model instead of an untyped bag.
const MULTI_VALUE_GROUPS: ("Performers" | "Tags")[] = ["Performers", "Tags"];

test("the six migrated fields are emitted under the C# property names, and no retired name is", () => {
  const emitted = normalizeOptions(fullyPopulatedBlob());

  for (const group of MULTI_VALUE_GROUPS) {
    for (const field of CSHARP_MULTI_VALUE_ID_FIELDS) {
      assert.ok(Object.hasOwn(emitted[group], field), `${group}.${field} missing from the wire`);
    }
    for (const retired of RETIRED_MULTI_VALUE_FIELDS) {
      assert.ok(!(retired in emitted[group]), `${group}.${retired} is still on the wire`);
    }
  }

  assert.ok(Object.hasOwn(emitted, CSHARP_TAG_DESTINATIONS));
  assert.ok(Object.hasOwn(emitted, CSHARP_EXCLUDE_TAG_IDS));
  assert.ok(!(RETIRED_EXCLUDE_TAGS in emitted));
});

test("each migrated field carries ids through load → save → load", () => {
  const blob = fullyPopulatedBlob();
  const loaded = normalizeOptions(blob);

  assert.deepEqual(loaded.Performers.WhitelistIds, [11]);
  assert.deepEqual(loaded.Performers.BlacklistIds, [22]);
  assert.deepEqual(loaded.Tags.WhitelistIds, [33]);
  assert.deepEqual(loaded.Tags.BlacklistIds, [44]);
  assert.deepEqual(loaded.TagDestinations, {
    5: { Root: "D:/lib", Template: "anime" },
    6: { Root: "E:/lib", Template: "docs" },
  });
  assert.deepEqual(loaded.ExcludeTagIds, [77]);

  const reloaded = normalizeOptions({ ...extractUnmodeledFields(blob), ...loaded });
  assert.deepEqual(reloaded, loaded);
});

test("a blob still holding the pre-migration names coerces every migrated field to empty", () => {
  // The stored shape before the backend's one-time name→id conversion runs: name lists and a
  // name-keyed destination map. Each must coerce to empty rather than throw or survive as strings —
  // a name that reached the backend now would bind nothing and revert the whole options object.
  const legacy = {
    Performers: { Whitelist: ["Jane Doe"], Blacklist: ["John Roe"] },
    Tags: { Whitelist: ["anime"], Blacklist: ["spam"] },
    TagDestinations: { Anime: "D:/anime" },
    ExcludeTags: ["nsfw"],
  };

  const loaded = normalizeOptions(legacy);

  assert.deepEqual(loaded.Performers.WhitelistIds, []);
  assert.deepEqual(loaded.Performers.BlacklistIds, []);
  assert.deepEqual(loaded.Tags.WhitelistIds, []);
  assert.deepEqual(loaded.Tags.BlacklistIds, []);
  assert.deepEqual(loaded.TagDestinations, {});
  assert.deepEqual(loaded.ExcludeTagIds, []);
  // And nothing carries the old values forward under their old names.
  assert.ok(!("ExcludeTags" in loaded));
  assert.ok(!("Whitelist" in loaded.Tags));
});

// ── hasUnmigratedNameRules: what stops the erasure above from reaching the store ──
//
// The test above pins that normalizeOptions empties every name-keyed rule. `extractUnmodeledFields`
// walks TOP-LEVEL keys only and both groups and TagDestinations are modeled, so the emptied shapes are
// what a save would persist — over rules nothing else keeps a copy of. This predicate is what the panel
// refuses to save on, so it must be true for exactly the blobs where that loss is real.

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
  // present, none holding a name. The backend has nothing to resolve in it, so blocking Save here
  // would lock the panel out permanently on a blob that will never be converted again.
  const realistic = {
    FilenameTemplate: "$title",
    Performers: { Separator: " ", Whitelist: [], Blacklist: [] },
    Tags: { Separator: " ", Whitelist: [], Blacklist: [] },
    TagDestinations: {},
    ExcludeTags: [],
  };

  assert.equal(hasUnmigratedNameRules(realistic), false);
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

test("a destination key that is not an id's invariant spelling counts as a name", () => {
  // The backend reads an int-spelled key as an ALREADY-migrated id and anything else as a tag name,
  // so the two must agree on the spelling exactly or the panel unblocks a save the backend still
  // has work for. int.MaxValue is the bound: past it the C# parse fails and the key is a name.
  const keySpellings: [string, boolean][] = [
    ["9", false],
    ["0", false],
    ["2147483647", false],
    ["09", true],
    ["-9", true],
    ["1e3", true],
    ["2147483648", true],
    ["9 ", true],
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

test("a malformed value for each migrated field yields an empty list or map, never a throw", () => {
  const malformed = {
    Performers: { WhitelistIds: "not-an-array", BlacklistIds: [{}, null] },
    Tags: { WhitelistIds: 42, BlacklistIds: ["3", Number.NaN] },
    TagDestinations: { 5: 12345, "not-a-number": "D:/x", 1.5: "D:/y" },
    ExcludeTagIds: { 0: 7 },
  };

  const loaded = normalizeOptions(malformed);

  assert.deepEqual(loaded.Performers.WhitelistIds, []);
  assert.deepEqual(loaded.Performers.BlacklistIds, []);
  assert.deepEqual(loaded.Tags.WhitelistIds, []);
  assert.deepEqual(loaded.Tags.BlacklistIds, []);
  assert.deepEqual(loaded.TagDestinations, {});
  assert.deepEqual(loaded.ExcludeTagIds, []);
});

test("the defaults and cloneDefaults both produce the id-valued shapes", () => {
  for (const options of [DEFAULT_OPTIONS, cloneDefaults()]) {
    for (const group of MULTI_VALUE_GROUPS) {
      assert.deepEqual(options[group].WhitelistIds, []);
      assert.deepEqual(options[group].BlacklistIds, []);
    }
    assert.deepEqual(options.TagDestinations, {});
    assert.deepEqual(options.ExcludeTagIds, []);
  }

  // A fresh install must accept an id without a coercion step the loaded path would not apply.
  const fresh = cloneDefaults();
  fresh.Tags.WhitelistIds.push(9);
  fresh.TagDestinations["9"] = { Root: "D:/lib", Template: "nine" };
  fresh.ExcludeTagIds.push(9);
  assert.deepEqual(normalizeOptions(fresh), fresh);
});

// --- The destination-map key codec ---
// Here rather than in a file of its own because the codec it covers lives beside the
// `destinationMap` coercion it has to agree with.

const A = { Root: "/lib", Template: "a" };
const B = { Root: "/lib", Template: "b" };

test("a number-keyed map becomes a string-keyed map preserving values", () => {
  assert.deepEqual(toStringKeyed({ 3: A, 12: B }), { 3: A, 12: B });
});

test("a round-trip through string keys restores number keys identically", () => {
  const original = { 3: A, 12: B };
  const back = fromStringKeyed(toStringKeyed(original));
  assert.deepEqual(back, original);
});

test("every back-converted key is an integer (value-equal with the backend's number keys)", () => {
  // JS object keys are always strings at the JS level, so a `typeof` check would be tautological;
  // the real invariant is that each key round-trips to an integer (no NaN/float survives).
  const back = fromStringKeyed({ 7: A, 42: B });
  assert.ok(Object.keys(back).every((k) => Number.isInteger(Number(k))));
  assert.deepEqual(back[7], A);
});

test("a non-integer key is dropped rather than producing a NaN key", () => {
  const back = fromStringKeyed({ x: A, 1.5: B, 9: A });
  assert.deepEqual(back, { 9: A });
});

test("a value that is not a destination is dropped on back-conversion", () => {
  // The string value is off-contract on purpose and the cast is what says so: it is exactly what a
  // blob written before destinations carried a root holds, and reading it as either half of the pair
  // would be a guess — a typed path is not a root the picker offers, and it is not a template either.
  const back = fromStringKeyed({
    4: "/media/legacy",
    5: A,
  } as unknown as Record<string, typeof A>);
  assert.deepEqual(back, { 5: A });
});

// --- What a one-library-path install stores as a destination root ---
//
// Here, beside the sentinel these functions choose against, because the pair is one decision: a
// destination the USER creates on such an install stores the library path itself, while the one-time
// conversion of an EXISTING install's *Where files go* stores the sentinel, so that upgrade moves
// nothing. Both halves have to hold or the rule is unreadable — auto-selecting on upgrade would
// relocate a library, and storing the sentinel on a new rule would leave a file outside the library
// skipped where the rule plainly names somewhere to put it. The conversion half is C# and is pinned at
// `OptionsMigrationLogicTests.AStoredRoot_BecomesTheLibraryPathHoldingIt_PlusTheRestAndTheOldFolder`
// `Template` (its closing pair of assertions); nothing below can reach it, which is why it is named
// rather than restated.

test("a rule created where Cove has one library path stores that path, not the sentinel", () => {
  assert.deepEqual(newDestination(["G:/library"]), { Root: "G:/library", Template: "" });
});

test("a rule created where Cove has several library paths stores the sentinel for the picker", () => {
  // Nothing to auto-select: the user picks, and until they do the value must not name one of them.
  assert.deepEqual(newDestination(["G:/library", "I:/library"]), { Root: "", Template: "" });
  assert.deepEqual(newDestination([]), { Root: "", Template: "" });
});

test("the default takes the sole library path once it names a folder, and not before", () => {
  const paths = ["G:/library"];
  // Blank template: the shipped "rename in place" state. A named root always relocates, so storing
  // one here would move every unmatched file to the top of the library.
  assert.equal(defaultDestinationRoot("", "", paths), "");
  assert.equal(defaultDestinationRoot("$studio", "", paths), "G:/library");
  // …and clearing the folder again returns it, so the round trip cannot strand a root nobody picked.
  assert.equal(defaultDestinationRoot("", "G:/library", paths), "");
});

test("the default's stored root is left alone wherever the picker is on screen", () => {
  // Several paths, none at all, and a root that is no longer one of them: three states in which the
  // value on screen is the user's own answer, and deriving one would overwrite it.
  assert.equal(
    defaultDestinationRoot("$studio", "I:/library", ["G:/library", "I:/library"]),
    "I:/library",
  );
  assert.equal(defaultDestinationRoot("$studio", "I:/library", []), "I:/library");
  assert.equal(defaultDestinationRoot("$studio", "E:/gone", ["G:/library"]), "E:/gone");
});

// --- Which library path a stored root names ---
//
// The backend now emits ONE spelling of each library path, so anything this panel writes from here on
// matches by plain equality; that agreement is pinned where it is decided, across the two producers,
// at `LibraryPathsEndpointTests.TheSpellingItEmits_IsTheSpellingADestinationRootIsStoredIn`. What is
// left for this side is the store an OLDER build wrote, where the endpoint handed back Cove's own
// platform spelling — measured on a real host, whose store held `I:\Downloads\P` under a rule the
// conversion had written as `I:/Downloads/P`. Both name one folder and both work; only the panel
// disagreed, and it told the user their working rules were skipped.

test("a root stored in Cove's own spelling still names its library path", () => {
  // Both values are the ones a live host actually held, not a composed pair.
  assert.equal(chosenLibraryPath("I:\\Downloads\\P", ["I:/Downloads/P"]), "I:/Downloads/P");
  assert.equal(chosenLibraryPath("I:/Downloads/P/", ["I:/Downloads/P"]), "I:/Downloads/P");
});

test("a root that is merely inside a library path, or outside every one, still names none", () => {
  // The tolerance forgives a separator, never a different folder — otherwise a root a user removed
  // from Cove would go on reading as fine. `…/P/videos` is the case that separates membership from
  // containment, which is the shape a library path RENAMED to something broader arrives in.
  assert.equal(chosenLibraryPath("I:\\Downloads\\P\\videos", ["I:/Downloads/P"]), undefined);
  assert.equal(chosenLibraryPath("E:/archive", ["I:/Downloads/P"]), undefined);
  assert.equal(chosenLibraryPath("I:/Downloads/P", []), undefined);
});

test("the default recognises its sole library path through the older spelling", () => {
  // The behaviour this buys: clearing the folder template returns the default to "nothing moves".
  // Reading the stored root as a stranger would strand it, and the panel hides the picker on a
  // one-path install, so there would be nothing on screen to correct it with.
  assert.equal(defaultDestinationRoot("", "G:\\library", ["G:/library"]), "");
  assert.equal(defaultDestinationRoot("$studio", "G:\\library", ["G:/library"]), "G:/library");
});
