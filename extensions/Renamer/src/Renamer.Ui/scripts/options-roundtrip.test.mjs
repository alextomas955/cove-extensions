/**
 * Round-trip + preservation contract for the options model. The runner compiles options.ts and
 * passes the compiled module path in OPTIONS_MODULE; importing the exact compiled artifact keeps the
 * test honest about what ships. The save merge is reproduced as `{ ...extras, ...options }` — the
 * literal merge RenameSettingsPanel.saveOptions uses — so a pass proves the SAME merge the panel runs.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.OPTIONS_MODULE);
const { normalizeOptions, extractUnmodeledFields, cloneDefaults, DEFAULT_OPTIONS } = mod;

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
      Whitelist: ["keep"],
      Blacklist: ["drop"],
      IgnoreGenders: ["unknown"],
      GenderOrder: ["female", "male"],
    },
    Tags: {
      Separator: "-",
      MaxCount: 2,
      OnOverflow: "KeepFirst",
      Sort: "None",
      Whitelist: ["anime"],
      Blacklist: ["spam"],
      IgnoreGenders: [],
      GenderOrder: [],
    },
    IllegalReplacement: "_",
    SpaceReplacement: ".",
    RemoveCharacters: ",#",
    Case: "Lower",
    AsciiTransliterate: true,
    FilenameMax: 200,
    FullPathMax: 240,
    DropOrder: ["tags", "studio"],
    OnlyOrganized: true,
    FilenameAsTitle: true,
    RequiredFields: ["title", "studio"],
    DuplicateSuffixFormat: "_{n}",
    AutoRenamerOnUpdate: true,
    StudioDestinations: { 7: "D:/studios/seven", 12: "E:/studios/twelve" },
    TagDestinations: { Anime: "D:/anime", Docs: "E:/docs" },
    PathDestinations: [
      { Pattern: "C:/in", Dest: "D:/out", IsRegex: false },
      { Pattern: "^C:/re/.*$", Dest: "E:/out", IsRegex: true },
    ],
    ExcludeTags: ["nsfw"],
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

const SAFETY_KNOBS = {
  FreeSpaceHeadroomBytes: 2147483648,
  CrossVolumeConcurrency: 4,
  SameVolumeConcurrency: 16,
};

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
  clone.TagDestinations.t = "x";
  clone.PathDestinations.push({ Pattern: "p", Dest: "d", IsRegex: false });
  clone.ExcludePaths.push({ Pattern: "p", IsRegex: false });
  clone.FieldReplacers.push({ TargetToken: "t", Find: "f", Replace: "r" });
  clone.ExcludeTags.push("x");
  clone.ExcludeStudioIds.push(99);
  clone.AllowedRoots.push("Z:/");
  clone.Articles.push("Der");
  clone.DropOrder.push("x");
  clone.RequiredFields.push("x");
  clone.Performers.IgnoreGenders.push("x");
  clone.Performers.GenderOrder.push("x");
  clone.Tags.IgnoreGenders.push("x");
  clone.Tags.GenderOrder.push("x");
  clone.Tags.Whitelist.push("x");

  assert.deepEqual(DEFAULT_OPTIONS, before);
});

test("unmodeled safety knobs survive the save merge unchanged across a load → save loop", () => {
  const blob = { ...fullyPopulatedBlob(), ...SAFETY_KNOBS };

  const extras = extractUnmodeledFields(blob);
  assert.equal(extras.FreeSpaceHeadroomBytes, SAFETY_KNOBS.FreeSpaceHeadroomBytes);
  assert.equal(extras.CrossVolumeConcurrency, SAFETY_KNOBS.CrossVolumeConcurrency);
  assert.equal(extras.SameVolumeConcurrency, SAFETY_KNOBS.SameVolumeConcurrency);

  const persisted = { ...extras, ...normalizeOptions(blob) };
  assert.equal(persisted.FreeSpaceHeadroomBytes, SAFETY_KNOBS.FreeSpaceHeadroomBytes);
  assert.equal(persisted.CrossVolumeConcurrency, SAFETY_KNOBS.CrossVolumeConcurrency);
  assert.equal(persisted.SameVolumeConcurrency, SAFETY_KNOBS.SameVolumeConcurrency);

  // A second load → save keeps them: the merge re-extracts and re-merges with no drift.
  const extras2 = extractUnmodeledFields(persisted);
  const persisted2 = { ...extras2, ...normalizeOptions(persisted) };
  assert.equal(persisted2.FreeSpaceHeadroomBytes, SAFETY_KNOBS.FreeSpaceHeadroomBytes);
  assert.equal(persisted2.CrossVolumeConcurrency, SAFETY_KNOBS.CrossVolumeConcurrency);
  assert.equal(persisted2.SameVolumeConcurrency, SAFETY_KNOBS.SameVolumeConcurrency);
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
