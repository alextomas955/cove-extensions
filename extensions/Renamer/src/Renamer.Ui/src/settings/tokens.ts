/**
 * The engine's token set, in the canonical `Tokens` constant order from
 * `src/Renamer/Engine/TemplateEngine.cs` (there is no Tokens.cs - TemplateEngine.cs owns the
 * `Tokens` class). Listing the engine's real names keeps the legend and the validator
 * single-sourced with what the engine actually resolves.
 *
 * It sits apart from the legend that renders it so the validator can read it without depending on
 * a view module.
 */

/**
 * A legend entry. `kind` drives insertion style; `insert` is the exact string spliced at the
 * caret when the chip is clicked.
 *
 *  - `core` tokens (`$title`, `$ext`) are effectively always-present, so they insert bare.
 *  - `optional` tokens insert pre-wrapped in one `{}` group whose leading separator + literals
 *    live inside the group, so the whole span collapses (engine `RenderGroup`) when the token
 *    resolves empty - no dangling `[]`, no stray separator. Spec-like tokens use the bracket
 *    style `{ [$token]}`; prose-like tokens use the dash style `{ - $token}`. NB: bare `$token`
 *    only - the engine has no `${token}` form.
 */
export interface TokenEntry {
  token: string;
  label: string;
  kind: "core" | "optional";
  insert: string;
}

/** Canonical token names + short labels, in TemplateEngine.cs `Tokens` declaration order. */
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
