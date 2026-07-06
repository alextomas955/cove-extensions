/**
 * Built-in starter templates. Clicking a preset chip sets `FilenameTemplate`
 * via the existing set() path so `dirty` flips and the existing debounced /preview-sample
 * re-renders — the live preview IS the feedback. Presets do not touch `FolderTemplate`
 * (folder-move stays opt-in).
 *
 * Every template uses BARE `$token` and wraps each optional token in a `{}` group (leading
 * separator + literals INSIDE the group) so no preset ever leaves dangling punctuation. The
 * engine has NO `${token}` form — never use it here.
 */
export interface Preset {
  label: string;
  filenameTemplate: string;
}

/** The starter presets shown as one-click chips in the settings panel. */
export const PRESETS: readonly Preset[] = [
  // The shipped default, offered as a chip so a user who edits the template can return to it in one
  // click. The string matches DEFAULT_OPTIONS.FilenameTemplate exactly so the chip and "Reset to
  // defaults" produce the identical template.
  { label: "Date – Title [Resolution]", filenameTemplate: "{$date - }$title{ [$resolution]}" },
  { label: "Title + resolution", filenameTemplate: "$title{ [$resolution]}" },
  { label: "Studio – Title [Res]", filenameTemplate: "$studio{ - $title}{ [$resolution]}" },
  { label: "Date – Title", filenameTemplate: "$date{ - $title}" },
  { label: "Performers – Title", filenameTemplate: "$performers{ - $title}" },
];
