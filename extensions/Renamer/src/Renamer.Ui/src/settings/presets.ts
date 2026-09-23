/**
 * Built-in starter templates. Clicking a preset chip sets `FilenameTemplate`
 * via the existing set() path so `dirty` flips and the existing debounced /preview-sample
 * re-renders - the live preview is the feedback. Presets do not touch `FolderTemplate`
 * (folder-move stays opt-in).
 *
 * Every template uses bare `$token` and wraps each optional token in a `{}` group (leading
 * separator + literals inside the group) so no preset ever leaves dangling punctuation. The
 * engine has no `${token}` form - never use it here.
 */
interface Preset {
  label: string;
  filenameTemplate: string;
}

/** The starter presets shown as one-click chips in the settings panel. */
export const PRESETS: readonly Preset[] = [
  // The shipped default's template, offered as a chip so a user who edits the template can get back
  // to it in one click.
  { label: "Date – Title [Resolution]", filenameTemplate: "{$date - }$title{ [$resolution]}" },
  { label: "Title + resolution", filenameTemplate: "$title{ [$resolution]}" },
  { label: "Studio – Title [Res]", filenameTemplate: "$studio{ - $title}{ [$resolution]}" },
  { label: "Date – Title", filenameTemplate: "$date{ - $title}" },
  { label: "Performers – Title", filenameTemplate: "$performers{ - $title}" },
];
