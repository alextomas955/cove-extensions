/**
 * Behavior contract for the pure file-settings logic. The runner (check-file-settings-logic.mjs) compiles
 * fileSettingsLogic.ts and passes the compiled module URL via FILE_SETTINGS_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.FILE_SETTINGS_LOGIC_MODULE);
const {
  FILE_SETTING_FIELDS,
  ALL_FILE_SETTINGS_OFF,
  FILE_SETTINGS_WARNING_HEADING,
  fileSettingsFromServer,
  fileSettingsWriteBody,
  anyFileSettingOn,
  onSettingLabels,
  fileSettingsWarning,
  sameFileSettings,
} = mod;

test("the four fields carry the exact PascalCase wire keys, grouped by endpoint (C4)", () => {
  const wire = FILE_SETTING_FIELDS.map((f) => f.wire);
  assert.deepEqual(wire, [
    "RenameMovies",
    "ReplaceIllegalCharacters",
    "AutoRenameFolders",
    "DeleteEmptyFolders",
  ]);
  // Naming vs media-management grouping matches the two config singletons.
  assert.deepEqual(
    FILE_SETTING_FIELDS.filter((f) => f.group === "naming").map((f) => f.key),
    ["renameMovies", "replaceIllegalCharacters"],
  );
  assert.deepEqual(
    FILE_SETTING_FIELDS.filter((f) => f.group === "mediaManagement").map((f) => f.key),
    ["autoRenameFolders", "deleteEmptyFolders"],
  );
  // Every field has a non-empty label + a non-empty risk line.
  for (const f of FILE_SETTING_FIELDS) {
    assert.ok(f.label.length > 0, `label for ${f.key}`);
    assert.ok(f.risk.length > 0, `risk for ${f.key}`);
  }
});

test("fileSettingsWriteBody emits ONLY the four PascalCase booleans — never a whole config object", () => {
  const body = fileSettingsWriteBody({
    renameMovies: true,
    replaceIllegalCharacters: false,
    autoRenameFolders: true,
    deleteEmptyFolders: false,
  });
  assert.deepEqual(Object.keys(body).sort(), [
    "AutoRenameFolders",
    "DeleteEmptyFolders",
    "RenameMovies",
    "ReplaceIllegalCharacters",
  ]);
  assert.equal(body.RenameMovies, true);
  assert.equal(body.ReplaceIllegalCharacters, false);
  assert.equal(body.AutoRenameFolders, true);
  assert.equal(body.DeleteEmptyFolders, false);
});

test("fileSettingsFromServer reads camelCase; null (not-loaded) on an unreadable shape", () => {
  const s = fileSettingsFromServer({
    renameMovies: true,
    replaceIllegalCharacters: false,
    autoRenameFolders: true,
    deleteEmptyFolders: false,
  });
  assert.deepEqual(s, {
    renameMovies: true,
    replaceIllegalCharacters: false,
    autoRenameFolders: true,
    deleteEmptyFolders: false,
  });
  // Not-loaded is null (never guessed all-off), so the section shows the "Test the connection" affordance.
  assert.equal(fileSettingsFromServer(null), null);
  assert.equal(fileSettingsFromServer("nope"), null);
  // A present-but-non-boolean field coerces to false (safe default), object still loads.
  assert.deepEqual(fileSettingsFromServer({ renameMovies: "yes" }), {
    renameMovies: false,
    replaceIllegalCharacters: false,
    autoRenameFolders: false,
    deleteEmptyFolders: false,
  });
});

test("anyFileSettingOn + warning: quiet when all off; names the on-toggles comma-joined when any on", () => {
  assert.equal(anyFileSettingOn(ALL_FILE_SETTINGS_OFF), false);
  assert.equal(fileSettingsWarning(ALL_FILE_SETTINGS_OFF), null);

  const on = { ...ALL_FILE_SETTINGS_OFF, renameMovies: true, autoRenameFolders: true };
  assert.equal(anyFileSettingOn(on), true);
  assert.deepEqual(onSettingLabels(on), ["Rename movie files", "Auto-rename folders"]);
  const w = fileSettingsWarning(on);
  assert.match(w, /Rename movie files, Auto-rename folders/); // comma-joined labels, display order
  assert.match(w, /acts on Cove's real files/i); // states the in-place risk
});

test("sameFileSettings is value equality (page-level dirty check)", () => {
  assert.equal(sameFileSettings(ALL_FILE_SETTINGS_OFF, { ...ALL_FILE_SETTINGS_OFF }), true);
  assert.equal(
    sameFileSettings(ALL_FILE_SETTINGS_OFF, { ...ALL_FILE_SETTINGS_OFF, deleteEmptyFolders: true }),
    false,
  );
});

test("FILE_SETTINGS_WARNING_HEADING is the design-locked heading", () => {
  assert.equal(FILE_SETTINGS_WARNING_HEADING, "Whisparr may change files in your library");
});
