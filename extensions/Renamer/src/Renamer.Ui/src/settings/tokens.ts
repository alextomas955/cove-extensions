/**
 * The engine's token set, in the `Tokens` constant order from
 * `src/Renamer/Engine/TemplateEngine.cs`, which owns that class. Listing the engine's own names
 * keeps the legend and the validator agreed with what the engine resolves.
 */

/**
 * A legend entry. `insert` is the exact string spliced in at the caret when the chip is clicked.
 *
 *  - A `core` token is always present, so it inserts bare.
 *  - An `optional` token inserts pre-wrapped in one `{}` group holding its leading separator and
 *    literals, so the whole span collapses when the token resolves empty and leaves no stray
 *    punctuation. The engine has no `${token}` form, so the bare `$token` spelling is the only one.
 */
export interface TokenEntry {
  token: string;
  label: string;
  kind: "core" | "optional";
  insert: string;
}

export const TOKENS: readonly TokenEntry[] = [
  { token: "$title", label: "Title", kind: "core", insert: "$title" },
  { token: "$studio", label: "Studio", kind: "optional", insert: "{ - $studio}" },
  {
    token: "$parentStudio",
    label: "Parent studio",
    kind: "optional",
    insert: "{ - $parentStudio}",
  },
  { token: "$studioCode", label: "Studio code", kind: "optional", insert: "{ - $studioCode}" },
  { token: "$director", label: "Director", kind: "optional", insert: "{ - $director}" },
  { token: "$bitrate", label: "Bitrate", kind: "optional", insert: "{ [$bitrate]}" },
  { token: "$date", label: "Date", kind: "optional", insert: "{ - $date}" },
  { token: "$year", label: "Year", kind: "optional", insert: "{ [$year]}" },
  { token: "$height", label: "Height", kind: "optional", insert: "{ [$height]}" },
  { token: "$width", label: "Width", kind: "optional", insert: "{ [$width]}" },
  {
    token: "$resolution",
    label: "Resolution (e.g. 1080p)",
    kind: "optional",
    insert: "{ [$resolution]}",
  },
  { token: "$videoCodec", label: "Video codec", kind: "optional", insert: "{ [$videoCodec]}" },
  { token: "$audioCodec", label: "Audio codec", kind: "optional", insert: "{ [$audioCodec]}" },
  { token: "$frameRate", label: "Frame rate", kind: "optional", insert: "{ [$frameRate]}" },
  { token: "$duration", label: "Duration", kind: "optional", insert: "{ [$duration]}" },
  { token: "$performers", label: "Performers", kind: "optional", insert: "{ - $performers}" },
  { token: "$tags", label: "Tags", kind: "optional", insert: "{ - $tags}" },
  { token: "$ext", label: "Extension", kind: "core", insert: "$ext" },
];
