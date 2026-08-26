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

## Picking a studio, tag or performer

Several settings below ask you to pick studios, tags or performers. Each one searches your library as
you type and lists the matches; click a match to add it, and click the **×** on a chip to remove it.

Each pick is stored by that studio, tag or performer's identity rather than by its name, so renaming
it in Cove keeps every rule that uses it, and two spellings of one name cannot route to two different
folders. These pickers never create a new studio, tag or performer - add it in Cove first, then pick
it here.

## Filename & destination

| Setting           | What it does                                                                                                             | Default                            |
| ----------------- | ------------------------------------------------------------------------------------------------------------------------ | ---------------------------------- |
| Filename template | The pattern used to build each new filename. Plain text plus `$tokens`. See [Naming templates](./templates).             | `{$date - }$title{ [$resolution]}` |
| Under             | Which of Cove's library paths the default destination measures from. Also offers _(the file's own library path)_.        | _(the file's own library path)_    |
| Folder template   | The pattern for the folders made under that root (use `/` for sub-folders). **Blank = rename in place, no folder move.** | _(blank)_                          |

The **preset** chips set the filename template to a starter pattern; the **live preview** shows the
result on sample items as you type.

### A destination is a root plus a folder template

Every destination in Renamer — the default above, the unorganized route, and each per-studio,
per-tag and source-path rule — has the same two parts:

- **Under**: one of the library paths you configured in Cove. You pick it from a list; you never type
  a path. Change that folder in Cove and the rule follows it.
- **Folder template**: the folders made underneath it, from the same `$tokens` a filename uses. Blank
  means the root itself.

Leaving **Under** as _(the file's own library path)_ measures the folder template from whichever
library path already holds the file, so one rule can tidy every library path in place. A file that is
under none of them has no root to measure from, so it is skipped and the dry run says so; add its
folder to Cove's library paths, or pick a library path for the destination instead.

If you remove a folder from Cove's library paths, every rule that named it stops and says so rather
than sending its items somewhere you did not choose. Re-pick a root, or add the folder back in Cove.

## What gets renamed

| Setting                                | What it does                                                                                                                                              | Default |
| -------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------- | ------- |
| Only rename organized items            | Skip items whose _Organized_ flag is off, so un-curated items don't get names. (Turning on the _Unorganized destination_ overrides this for those items.) | Off     |
| Use filename as title when none is set | When an item has no title, derive `$title` from the file's current basename instead of skipping it.                                                       | On      |
| Required fields                        | Token names that must resolve to a non-empty value, or the item is skipped. Empty = no gate.                                                              | `title` |

## Run & automation

| Setting               | What it does                                                                                | Default |
| --------------------- | ------------------------------------------------------------------------------------------- | ------- |
| Auto-rename on update | Re-rename an item automatically when Cove raises a `video.updated` / `image.updated` event. | Off     |

This section also holds the **Dry run** and **Rename all files** actions (see the
[User guide](./guide#preview-with-a-dry-run)). Those run a rename; they aren't saved settings.

## Token settings

These cards appear only when your template uses the matching token.

### Performers (`$performers`) and Tags (`$tags`)

Both are multi-value lists shaped by the same options (a few apply to performers only):

| Setting                            | What it does                                                                                                                              | Default         |
| ---------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- | --------------- |
| Separator                          | Text inserted between joined items.                                                                                                       | `" "` (a space) |
| Max count                          | Maximum items to include; `0` = unlimited.                                                                                                | `0`             |
| On overflow                        | When over the max: _Drop all_ or _Keep the first N_.                                                                                      | Drop all        |
| Sort                               | Order before joining: Name (A→Z), Keep original order, and — performers only — By internal id, Favorites first then name.                 | Name (A→Z)      |
| Whitelist                          | If non-empty, only the performers or tags you pick here are kept. Each is remembered by identity, so renaming one in Cove keeps the rule. | _(empty)_       |
| Blacklist                          | The performers or tags you pick here are removed. Remembered by identity, the same way as the whitelist.                                  | _(empty)_       |
| Ignore genders _(performers only)_ | Genders to drop before the max-count limit. A performer with no gender set is always kept.                                                | _(empty)_       |
| Gender order _(performers only)_   | Preferred gender order, most-preferred first; controls who survives the max-count limit.                                                  | _(empty)_       |

### Date & duration format

| Setting         | What it does                                                                                                  | Default      |
| --------------- | ------------------------------------------------------------------------------------------------------------- | ------------ |
| Date format     | .NET date format for `$date`. Options include `yyyy-MM-dd`, `yyyy`, `MM-dd-yyyy`, `dd.MM.yyyy`, `yyyy.MM.dd`. | `yyyy-MM-dd` |
| Duration format | .NET duration format for `$duration`, e.g. `hh-mm-ss`, `hh.mm.ss`, `mm-ss`.                                   | `hh-mm-ss`   |

## Destination routing

Renamer decides where each item goes by checking rules in a fixed **precedence order**:

> **Excludes → Unorganized → Tag → Studio (including parent studios) → Source path**

An item that matches no rule at all takes the default destination — the _Under_ and _Folder
template_ pair in [Filename & destination](#filename--destination). A rule that does match replaces
that default outright: its own folder template is the only one rendered, never appended to the
default's.

Within a category the first matching rule (in your order) wins; excludes always run first. The
order of the cards below in the UI is for convenience and does not change this precedence.

### Unorganized destination

| Setting                                          | What it does                                                                                                                                                                                   | Default                                |
| ------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------- |
| Route unorganized items to their own destination | Send items whose _Organized_ flag is off here instead of skipping them. Resolved before the tag, studio and source-path rules, and it overrides _Only rename organized items_ for those items. | Off                                    |
| Under / Folder template                          | The destination those items go to, in the two parts every destination has.                                                                                                                     | _(the file's own library path)_, blank |

Turning the toggle **off** is not the same as leaving the destination blank: off means there is no
unorganized route at all, so _Only rename organized items_ decides what happens to those items.

### Per-studio and per-tag destinations

| Setting                 | What it does                                                                                                                                        | Default  |
| ----------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------- | -------- |
| Per-studio destinations | Map a studio → a destination. Remembered by the studio's identity, so a rename in Cove keeps the rule and never splits one studio across two trees. | _(none)_ |
| Per-tag destinations    | Map a tag → a destination. Remembered by the tag's identity, the same way as a studio rule.                                                         | _(none)_ |

### Advanced routing & safety

| Setting                  | What it does                                                                                                                                                 | Default   |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------ | --------- |
| Allowed roots            | An optional **narrowing**: when set, a rename may only write inside these absolute folders, even where a destination would allow more. Empty = no narrowing. | _(empty)_ |
| Source-path destinations | Ordered rules matching an item's source path (exact, or a regex) → a destination. Exact matches are tried before regex.                                      | _(none)_  |

### Sidecar files and empty folders

| Setting                                              | What it does                                                                                                  | Default   |
| ---------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- | --------- |
| Also move sidecar files with these extensions        | Extensions whose same-name neighbor file moves alongside the primary (e.g. `srt` for subtitles).              | _(empty)_ |
| Delete the source folder when a move leaves it empty | After a move empties the source folder, delete it (only-if-empty, non-recursive). Undo will not re-create it. | Off       |

## Advanced

Collapsed by default.

### Clean up the name

| Setting                        | What it does                                                                                                                                                                                    | Default |
| ------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------- |
| Illegal-char replacement       | What to do with characters the OS forbids in a filename: **strip** them, or **replace** each with a string you provide.                                                                         | Strip   |
| Space replacement              | **Keep** spaces, or **replace** each space with a string (e.g. `.` or `_`).                                                                                                                     | Keep    |
| Remove characters              | Literal characters deleted from the name outright (not a regex).                                                                                                                                | `,#`    |
| Case                           | Case transform applied to the whole name: None, lower case, or Title Case.                                                                                                                      | None    |
| ASCII transliterate            | Convert accented characters to their ASCII equivalents (e.g. `é` → `e`).                                                                                                                        | Off     |
| Normalize punctuation to ASCII | Fold typographic punctuation to plain ASCII: curly quotes → straight quotes, en/em dashes → a hyphen, ellipsis → three dots. Letters and accents are untouched (that is _ASCII transliterate_). | On      |

### Length & collisions

| Setting                 | What it does                                                                                        | Default                                                                                     |
| ----------------------- | --------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| Filename max length     | Maximum length of the filename.                                                                     | `255`                                                                                       |
| Full-path max length    | Maximum length of the full path.                                                                    | `259`                                                                                       |
| Drop order              | When a name is too long, the order in which fields are dropped to fit (first listed dropped first). | `videoCodec, audioCodec, frameRate, resolution, tags, studioCode, studio, performers, date` |
| Duplicate suffix format | Suffix added before the extension when the target name is taken; `{n}` is the collision counter.    | `" ({n})"` → `name (1).mp4`                                                                 |

### Cross-drive concurrency

| Setting                  | What it does                                                                                                                                                                                                         | Default |
| ------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------- |
| Cross-volume concurrency | How many files to copy across drives at once. Leave at 2 for regular hard drives; raise to 4–8 if both drives are SSDs. Higher is not always faster — on spinning disks it can be slower. Clamped to 1–16 in the UI. | `2`     |
| Same-volume concurrency  | How many same-drive renames to run at once (these are instant; the default is fine). Clamped to 1–16 in the UI.                                                                                                      | `8`     |

### Excludes

| Setting                | What it does                                                                                                      | Default   |
| ---------------------- | ----------------------------------------------------------------------------------------------------------------- | --------- |
| Exclude by tag         | Items carrying any of these tags are excluded from renaming/moving (evaluated first). Remembered by tag identity. | _(empty)_ |
| Exclude by studio      | Items whose studio (or a parent studio) matches are excluded. Remembered by studio identity.                      | _(empty)_ |
| Exclude by source path | Ordered exact-or-regex source-path rules; a match excludes the item.                                              | _(empty)_ |

### Field rewriting & name shaping

| Setting                               | What it does                                                                                          | Default      |
| ------------------------------------- | ----------------------------------------------------------------------------------------------------- | ------------ |
| Per-token replacements                | Literal find/replace rules applied to a specific token's value before other shaping (not a regex).    | _(none)_     |
| Strip leading article                 | Remove one leading article from `$title` (`The Matrix` → `Matrix`).                                   | Off          |
| Articles                              | The articles eligible for stripping.                                                                  | `The, A, An` |
| Squeeze studio names                  | Remove all spaces from `$studio` (`Studio Ghibli` → `StudioGhibli`) so one studio maps to one folder. | Off          |
| Drop a performer already in the title | Drop a performer whose name appears as a whole word in `$title`.                                      | Off          |
| Collapse repeated folder segments     | Collapse consecutive duplicate folder segments (`/Foo/Foo/Bar` → `/Foo/Bar`). Folder path only.       | On           |

## Advanced settings not shown in the UI

This is persisted but has **no control in the settings panel** — it exists for unusual cross-drive
setups and is safe to leave at its default. Changing it requires editing the extension's stored
options directly.

| Setting             | What it does                                                                                                   | Default |
| ------------------- | -------------------------------------------------------------------------------------------------------------- | ------- |
| Free-space headroom | Bytes kept free on each destination volume before a cross-drive batch proceeds (gates cross-drive moves only). | 1 GiB   |
