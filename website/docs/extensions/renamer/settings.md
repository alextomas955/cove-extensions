---
id: settings
title: Settings reference
sidebar_position: 3
---

Every Renamer setting, grouped by the section it appears in on the **Rename** settings tab
(Settings → Extensions → Rename). Defaults are what a fresh install uses. For how these fit
together in practice, start with the [User guide](./guide); for the template tokens, see
[Naming templates](./templates).

Settings are saved as one block when you click **Save**; **Discard** reverts unsaved edits.

## Filename & destination

| Setting | What it does | Default |
| --- | --- | --- |
| Filename template | The pattern used to build each new filename. Plain text plus `$tokens`. See [Naming templates](./templates). | `{$date - }$title{ [$resolution]}` |
| Folder template | The pattern for the destination folder path (use `/` for sub-folders). **Blank = rename in place, no folder move.** | *(blank)* |

The **preset** chips set the filename template to a starter pattern; the **live preview** shows the
result on sample items as you type. Moving files to a folder outside the item's own source folder
also requires an entry under [Allowed roots](#destination-routing).

## What gets renamed

| Setting | What it does | Default |
| --- | --- | --- |
| Only rename organized items | Skip items whose *Organized* flag is off, so un-curated items don't get names. (A configured *Unorganized destination* overrides this for those items.) | Off |
| Use filename as title when none is set | When an item has no title, derive `$title` from the file's current basename instead of skipping it. | On |
| Required fields | Token names that must resolve to a non-empty value, or the item is skipped. Empty = no gate. | `title` |

## Run & automation

| Setting | What it does | Default |
| --- | --- | --- |
| Auto-rename on update | Re-rename an item automatically when Cove raises a `video.updated` / `image.updated` event. | Off |

This section also holds the **Dry run** and **Rename all files** actions (see the
[User guide](./guide#preview-with-a-dry-run)). Those run a rename; they aren't saved settings.

## Token settings

These cards appear only when your template uses the matching token.

### Performers (`$performers`) and Tags (`$tags`)

Both are multi-value lists shaped by the same options (a few apply to performers only):

| Setting | What it does | Default |
| --- | --- | --- |
| Separator | Text inserted between joined items. | `" "` (a space) |
| Max count | Maximum items to include; `0` = unlimited. | `0` |
| On overflow | When over the max: *Drop all* or *Keep the first N*. | Drop all |
| Sort | Order before joining: Name (A→Z), Keep original order, and — performers only — By internal id, Favorites first then name. | Name (A→Z) |
| Whitelist | If non-empty, only these values are kept (case-insensitive). | *(empty)* |
| Blacklist | These values are removed (case-insensitive). | *(empty)* |
| Ignore genders *(performers only)* | Genders to drop before the max-count limit. A performer with no gender set is always kept. | *(empty)* |
| Gender order *(performers only)* | Preferred gender order, most-preferred first; controls who survives the max-count limit. | *(empty)* |

### Date & duration format

| Setting | What it does | Default |
| --- | --- | --- |
| Date format | .NET date format for `$date`. Options include `yyyy-MM-dd`, `yyyy`, `MM-dd-yyyy`, `dd.MM.yyyy`, `yyyy.MM.dd`. | `yyyy-MM-dd` |
| Duration format | .NET duration format for `$duration`, e.g. `hh-mm-ss`, `hh.mm.ss`, `mm-ss`. | `hh-mm-ss` |

## Destination routing

Renamer decides where each item goes by checking rules in a fixed **precedence order**:

> **Excludes → Unorganized → Tag → Studio (including parent studios) → Source path → Default**

Within a category the first matching rule (in your order) wins; excludes always run first. The
order of the cards below in the UI is for convenience and does not change this precedence.

### Default & unorganized destinations

| Setting | What it does | Default |
| --- | --- | --- |
| Default destination | The root folder for an item that matched no other rule. Honored **only** when *Relocate unmatched items* is on. | *(blank)* |
| Unorganized destination | The route for items whose *Organized* flag is off (resolved before tag/studio/path). Overrides *Only rename organized items* for those items. | *(blank)* |
| Relocate unmatched items to the default destination | Hard gate for default-relocate. Ships **off** — default-relocate has whole-library reach and undo is the only recovery. | Off |

### Per-studio and per-tag destinations

| Setting | What it does | Default |
| --- | --- | --- |
| Per-studio destinations | Map a studio → a destination root. Keyed on the studio's stable id, so a name typo never splits one studio across two trees. | *(none)* |
| Per-tag destinations | Map a tag name → a destination root (case-insensitive). | *(none)* |

### Advanced routing & safety

| Setting | What it does | Default |
| --- | --- | --- |
| Allowed roots | The absolute folders a rename is permitted to write into. Empty = confine each item to its own source folder (a rooted folder template is refused). Add a root to opt in to moving files there. | *(empty)* |
| Source-path destinations | Ordered rules matching an item's source path (exact, or a regex) → a destination root. Exact matches are tried before regex. | *(none)* |

### Sidecar files and empty folders

| Setting | What it does | Default |
| --- | --- | --- |
| Also move sidecar files with these extensions | Extensions whose same-name neighbor file moves alongside the primary (e.g. `srt` for subtitles). | *(empty)* |
| Delete the source folder when a move leaves it empty | After a move empties the source folder, delete it (only-if-empty, non-recursive). Undo will not re-create it. | Off |

## Advanced

Collapsed by default.

### Clean up the name

| Setting | What it does | Default |
| --- | --- | --- |
| Illegal-char replacement | What to do with characters the OS forbids in a filename: **strip** them, or **replace** each with a string you provide. | Strip |
| Space replacement | **Keep** spaces, or **replace** each space with a string (e.g. `.` or `_`). | Keep |
| Remove characters | Literal characters deleted from the name outright (not a regex). | `,#` |
| Case | Case transform applied to the whole name: None, lower case, or Title Case. | None |
| ASCII transliterate | Convert accented characters to their ASCII equivalents (e.g. `é` → `e`). | Off |
| Normalize punctuation to ASCII | Fold typographic punctuation to plain ASCII: curly quotes → straight quotes, en/em dashes → a hyphen, ellipsis → three dots. Letters and accents are untouched (that is *ASCII transliterate*). | On |

### Length & collisions

| Setting | What it does | Default |
| --- | --- | --- |
| Filename max length | Maximum length of the filename. | `255` |
| Full-path max length | Maximum length of the full path. | `259` |
| Drop order | When a name is too long, the order in which fields are dropped to fit (first listed dropped first). | `videoCodec, audioCodec, frameRate, resolution, tags, studioCode, studio, performers, date` |
| Duplicate suffix format | Suffix added before the extension when the target name is taken; `{n}` is the collision counter. | `" ({n})"` → `name (1).mp4` |

### Excludes

| Setting | What it does | Default |
| --- | --- | --- |
| Exclude by tag | Items carrying any of these tags are excluded from renaming/moving (evaluated first). | *(empty)* |
| Exclude by studio | Items whose studio (or a parent studio) matches are excluded. Keyed on stable studio id. | *(empty)* |
| Exclude by source path | Ordered exact-or-regex source-path rules; a match excludes the item. | *(empty)* |

### Field rewriting & name shaping

| Setting | What it does | Default |
| --- | --- | --- |
| Per-token replacements | Literal find/replace rules applied to a specific token's value before other shaping (not a regex). | *(none)* |
| Strip leading article | Remove one leading article from `$title` (`The Matrix` → `Matrix`). | Off |
| Articles | The articles eligible for stripping. | `The, A, An` |
| Squeeze studio names | Remove all spaces from `$studio` (`Studio Ghibli` → `StudioGhibli`) so one studio maps to one folder. | Off |
| Drop a performer already in the title | Drop a performer whose name appears as a whole word in `$title`. | Off |
| Collapse repeated folder segments | Collapse consecutive duplicate folder segments (`/Foo/Foo/Bar` → `/Foo/Bar`). Folder path only. | On |

## Advanced settings not shown in the UI

These are persisted but have **no control in the settings panel** — they exist for unusual
cross-drive setups and are safe to leave at their defaults. Changing them requires editing the
extension's stored options directly.

| Setting | What it does | Default |
| --- | --- | --- |
| Free-space headroom | Bytes kept free on each destination volume before a cross-drive batch proceeds (gates cross-drive moves only). | 1 GiB |
| Cross-volume concurrency | Max simultaneous cross-drive transfers per source→destination disk pair. | `2` |
| Same-volume concurrency | Max simultaneous same-drive renames in a batch (`≤ 0` = unbounded). | `8` |
