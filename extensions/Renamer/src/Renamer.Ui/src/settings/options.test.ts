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
    StudioDestinations: { 7: "D:/studios/seven", 12: "E:/studios/twelve" },
    TagDestinations: { 14: "D:/anime", 15: "E:/docs" },
    PathDestinations: [
      { Pattern: "C:/in", Dest: "D:/out", IsRegex: false },
      { Pattern: "^C:/re/.*$", Dest: "E:/out", IsRegex: true },
    ],
    ExcludeTagIds: [31],
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
  assert.deepEqual(loaded.TagDestinations, { 14: "D:/anime", 15: "E:/docs" });
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

  clone.StudioDestinations[1] = "x";
  clone.TagDestinations[2] = "x";
  clone.PathDestinations.push({ Pattern: "p", Dest: "d", IsRegex: false });
  clone.ExcludePaths.push({ Pattern: "p", IsRegex: false });
  clone.FieldReplacers.push({ TargetToken: "t", Find: "f", Replace: "r" });
  clone.ExcludeTagIds.push(77);
  clone.ExcludeStudioIds.push(99);
  clone.AllowedRoots.push("Z:/");
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

test("a number-keyed destination map becomes a string-keyed map preserving values", () => {
  assert.deepEqual(toStringKeyed({ 3: "/a", 12: "/b" }), { 3: "/a", 12: "/b" });
});

test("a round-trip through the editor's string keys restores number keys identically", () => {
  const original = { 3: "/a", 12: "/b" };
  assert.deepEqual(fromStringKeyed(toStringKeyed(original)), original);
});

test("a non-integer editor key is dropped rather than producing a NaN key", () => {
  assert.deepEqual(fromStringKeyed({ x: "/a", "1.5": "/b", "9": "/c" }), { 9: "/c" });
});

test("a non-string editor value is dropped on back-conversion", () => {
  // Cast because the drop is exactly what a hand-edited or legacy blob makes reachable, and the
  // parameter type is what forbids it: a well-typed caller cannot get here.
  const back = fromStringKeyed({ 4: 12, 5: "/ok" } as unknown as Record<string, string>);
  assert.deepEqual(back, { 5: "/ok" });
});

test("every destination-map key a save persists parses as an integer", () => {
  // The backend binds both maps as `Dictionary<int, string>` and answers a bind failure with
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
    TagDestinations: { Anime: "D:/anime" },
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
