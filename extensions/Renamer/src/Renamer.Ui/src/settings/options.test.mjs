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
  cloneDefaults,
  DEFAULT_OPTIONS,
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
    StudioDestinations: { 7: "D:/studios/seven", 12: "E:/studios/twelve" },
    TagDestinations: { 5: "D:/anime", 6: "E:/docs" },
    PathDestinations: [
      { Pattern: "C:/in", Dest: "D:/out", IsRegex: false },
      { Pattern: "^C:/re/.*$", Dest: "E:/out", IsRegex: true },
    ],
    ExcludeTagIds: [77],
    ExcludeStudioIds: [3, 9],
    ExcludePaths: [{ Pattern: "C:/skip", IsRegex: false }],
    AllowedRoots: ["D:/", "E:/"],
    AssociatedExtensions: ["srt", "vtt"],
    DefaultDestination: "D:/default",
    UnorganizedDestination: "D:/unorganized",
    EnableDefaultRelocate: true,
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
  assert.deepEqual(loaded.StudioDestinations, { 7: "D:/studios/seven", 12: "E:/studios/twelve" });
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
  const before = JSON.parse(JSON.stringify(DEFAULT_OPTIONS));
  const clone = cloneDefaults();

  clone.StudioDestinations[1] = "x";
  clone.TagDestinations[2] = "x";
  clone.PathDestinations.push({ Pattern: "p", Dest: "d", IsRegex: false });
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

  const persisted = { ...extras, ...normalizeOptions(blob) };
  assert.equal(persisted.FreeSpaceHeadroomBytes, UNMODELED_KNOB.FreeSpaceHeadroomBytes);
  assert.equal(persisted.CrossVolumeConcurrency, 4);
  assert.equal(persisted.SameVolumeConcurrency, 16);

  // A second load → save keeps them: the merge re-extracts and re-merges with no drift.
  const extras2 = extractUnmodeledFields(persisted);
  assert.ok(!("CrossVolumeConcurrency" in extras2));
  assert.ok(!("SameVolumeConcurrency" in extras2));
  const persisted2 = { ...extras2, ...normalizeOptions(persisted) };
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
    StudioDestinations: { 7: "D:/studios/seven" },
  };

  const loaded = normalizeOptions(oldBlob);
  assert.equal(loaded.EnableStudioDestinations, false);
  assert.equal(loaded.EnableTagDestinations, false);
  assert.equal(loaded.EnableAdvancedRouting, false);
});

test("a stale camelCase duplicate key is dropped by normalizeOptions", () => {
  const blob = {
    StudioDestinations: { 7: "D:/canonical" },
    studioDestinations: { 7: "D:/stale" },
  };

  const normalized = normalizeOptions(blob);
  assert.deepEqual(normalized.StudioDestinations, { 7: "D:/canonical" });
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

test("the six migrated fields are emitted under the C# property names, and no retired name is", () => {
  const emitted = normalizeOptions(fullyPopulatedBlob());

  for (const group of ["Performers", "Tags"]) {
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
  assert.deepEqual(loaded.TagDestinations, { 5: "D:/anime", 6: "E:/docs" });
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
    for (const group of ["Performers", "Tags"]) {
      assert.deepEqual(options[group].WhitelistIds, []);
      assert.deepEqual(options[group].BlacklistIds, []);
    }
    assert.deepEqual(options.TagDestinations, {});
    assert.deepEqual(options.ExcludeTagIds, []);
  }

  // A fresh install must accept an id without a coercion step the loaded path would not apply.
  const fresh = cloneDefaults();
  fresh.Tags.WhitelistIds.push(9);
  fresh.TagDestinations[9] = "D:/nine";
  fresh.ExcludeTagIds.push(9);
  assert.deepEqual(normalizeOptions(fresh), fresh);
});
