/**
 * Built-in starter templates. A preset sets the filename template only, so moving files into
 * folders stays something the user opts into.
 *
 * Every template uses the bare `$token` spelling, which is the only one the engine has, and wraps
 * each optional token in a `{}` group holding its leading separator, so no preset can leave
 * dangling punctuation.
 */
interface Preset {
  label: string;
  filenameTemplate: string;
}

export const PRESETS: readonly Preset[] = [
  // The shipped default's template, offered as a chip so a user who edits the template can get back
  // to it in one click.
  { label: "Date – Title [Resolution]", filenameTemplate: "{$date - }$title{ [$resolution]}" },
  { label: "Title + resolution", filenameTemplate: "$title{ [$resolution]}" },
  { label: "Studio – Title [Res]", filenameTemplate: "$studio{ - $title}{ [$resolution]}" },
  { label: "Date – Title", filenameTemplate: "$date{ - $title}" },
  { label: "Performers – Title", filenameTemplate: "$performers{ - $title}" },
];
