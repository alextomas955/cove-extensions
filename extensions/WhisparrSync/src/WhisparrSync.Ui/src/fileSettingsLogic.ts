/**
 * Pure, DOM-free logic behind the Whisparr file-settings editor (§3.7): the four file-affecting toggles,
 * their PascalCase wire fields, the read shaper for the `/file-settings` response, the write-body builder,
 * and the in-place-risk warning copy. Extracted so all of it is unit-testable without a DOM, and kept
 * import-free (no React, no SDK) so the offline gate compiles it in isolation like syncHealthLogic.ts.
 *
 * WIRE CONTRACT (C4): the WRITE body is PascalCase (`RenameMovies`, `ReplaceIllegalCharacters`,
 * `AutoRenameFolders`, `DeleteEmptyFolders`) to match the C# `WhisparrFileSettingsRequest` record; the READ
 * response comes back camelCase (the server's `MonitorResponseJsonOptions`). The server whitelists exactly
 * these four booleans and does the read-modify-write, so the UI never sends a whole config object.
 */

/** The UI model key for each toggle (camelCase, mirrors the GET response). */
export type FileSettingKey =
  "renameMovies" | "replaceIllegalCharacters" | "autoRenameFolders" | "deleteEmptyFolders";

/** The PascalCase wire field for each toggle (the WRITE body, matching the C# request record). */
export type FileSettingWireKey =
  "RenameMovies" | "ReplaceIllegalCharacters" | "AutoRenameFolders" | "DeleteEmptyFolders";

/** The four toggle values as the section holds them. */
export type FileSettings = Record<FileSettingKey, boolean>;

/** The endpoint group a toggle belongs to — used to sub-group the section by its Whisparr config singleton. */
export type FileSettingGroup = "naming" | "mediaManagement";

/** One toggle's full descriptor: model key, wire field, human label, one-line risk, and its endpoint group. */
export interface FileSettingField {
  key: FileSettingKey;
  wire: FileSettingWireKey;
  label: string;
  /** The one-line risk shown under the toggle — why turning it ON is dangerous under in-place sync. */
  risk: string;
  group: FileSettingGroup;
}

/**
 * The four toggles, in display order, grouped by their Whisparr config endpoint. Naming toggles ride
 * `/config/naming`; media-management toggles ride `/config/mediamanagement`. The labels + risk copy are
 * design-locked from 29-UI-SPEC § Edit Whisparr File-Settings.
 */
export const FILE_SETTING_FIELDS: readonly FileSettingField[] = [
  {
    key: "renameMovies",
    wire: "RenameMovies",
    label: "Rename movie files",
    risk: "Whisparr renames files in the shared library.",
    group: "naming",
  },
  {
    key: "replaceIllegalCharacters",
    wire: "ReplaceIllegalCharacters",
    label: "Replace illegal characters",
    risk: "Whisparr rewrites filenames.",
    group: "naming",
  },
  {
    key: "autoRenameFolders",
    wire: "AutoRenameFolders",
    label: "Auto-rename folders",
    risk: "Whisparr renames folders Cove points at.",
    group: "mediaManagement",
  },
  {
    key: "deleteEmptyFolders",
    wire: "DeleteEmptyFolders",
    label: "Delete empty folders",
    risk: "Whisparr removes folders in the shared tree.",
    group: "mediaManagement",
  },
];

/** Every toggle off — the safe default and the shape a not-loaded section never renders (it renders the affordance). */
export const ALL_FILE_SETTINGS_OFF: FileSettings = {
  renameMovies: false,
  replaceIllegalCharacters: false,
  autoRenameFolders: false,
  deleteEmptyFolders: false,
};

/** The section warning heading — advisory (amber), design-locked. */
export const FILE_SETTINGS_WARNING_HEADING = "Whisparr may change files in your library";

/**
 * Read the four booleans from an untrusted `/file-settings` response (camelCase), or null when the shape is
 * unreadable — null is the not-loaded signal the section uses to show the "Test the connection" affordance
 * rather than guessing all-off. A present-but-non-boolean field coerces to false (safe default).
 */
export function fileSettingsFromServer(raw: unknown): FileSettings | null {
  if (!raw || typeof raw !== "object") return null;
  const r = raw as Record<string, unknown>;
  const bool = (v: unknown): boolean => v === true;
  return {
    renameMovies: bool(r.renameMovies),
    replaceIllegalCharacters: bool(r.replaceIllegalCharacters),
    autoRenameFolders: bool(r.autoRenameFolders),
    deleteEmptyFolders: bool(r.deleteEmptyFolders),
  };
}

/**
 * Build the WRITE body for `POST /file-settings`: exactly the four PascalCase booleans, nothing else. The
 * server whitelists these and does the read-modify-write, so this never carries a whole config object (the
 * config-wipe mitigation, T-29-06-02).
 */
export function fileSettingsWriteBody(settings: FileSettings): Record<FileSettingWireKey, boolean> {
  return {
    RenameMovies: settings.renameMovies,
    ReplaceIllegalCharacters: settings.replaceIllegalCharacters,
    AutoRenameFolders: settings.autoRenameFolders,
    DeleteEmptyFolders: settings.deleteEmptyFolders,
  };
}

/** True when any file-affecting toggle is on — the condition that surfaces the in-place-risk warning. */
export function anyFileSettingOn(settings: FileSettings): boolean {
  return FILE_SETTING_FIELDS.some((f) => settings[f.key]);
}

/** The human labels of the on-toggles, in display order — the `{list}` in the warning body. */
export function onSettingLabels(settings: FileSettings): string[] {
  return FILE_SETTING_FIELDS.filter((f) => settings[f.key]).map((f) => f.label);
}

/**
 * The in-place-risk warning body, or null when nothing is on. Names the on-toggles and states the in-place
 * consequence — turning them on means Whisparr acts on Cove's real files (§6b).
 */
export function fileSettingsWarning(settings: FileSettings): string | null {
  if (!anyFileSettingOn(settings)) return null;
  const list = onSettingLabels(settings).join(", ");
  return `In-place sync means Whisparr acts on Cove's real files. These settings are on: ${list}. Turn them off here unless you want Whisparr to rename or remove files.`;
}

/** Value equality for the page-level dirty check. */
export function sameFileSettings(a: FileSettings, b: FileSettings): boolean {
  return FILE_SETTING_FIELDS.every((f) => a[f.key] === b[f.key]);
}
