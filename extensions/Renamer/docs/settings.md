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

Any setting that picks a tag, performer or studio searches your library **as you type** — type at
least one character to see matches; an empty field lists nothing. Each pick is stored as that item's
stable id, so renaming it in Cove keeps the rule pointed at it. A search that fails reads the same as
one that found nothing (_No tags found_), so if a value you know exists does not appear, change the
search text and try again.

## Filename & destination

| Setting           | What it does                                                                                                                                                                                                                         | Default                            |
| ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------- |
| Filename template | The pattern used to build each new filename. Plain text plus `$tokens`. See [Naming templates](./templates).                                                                                                                         | `{$date - }$title{ [$resolution]}` |
| Under             | Which of Cove's library paths this destination measures from. Shown only when Cove has more than one; with one there is nothing to choose and that path is used. **_(the file's own library path)_ = whichever one holds the file.** | _(the file's own library path)_    |
| Folder template   | The pattern for the folder path made under it (use `/` for sub-folders). **Blank = rename in place, no folder move.**                                                                                                                | _(blank)_                          |

The **preset** chips set the filename template to a starter pattern; the **live preview** shows the
result on sample items as you type.

### Where a file lands

Every destination has the same two parts, and you never type a path:

- **Under** — a root you pick from **Cove's own library paths**, or _(the file's own library path)_,
  meaning whichever one holds the file being renamed.
- **Folder template** — a relative pattern made underneath it, e.g. `$studio / $year`.

A destination **names a place**. Every item that matches goes there, wherever that item is sitting
now — a rule pointing at `/media/archive` pulls in matching files from `/media/library` as readily as
it keeps the ones already there. _(the file's own library path)_ is the one choice that does not work
that way, and it is offered for exactly that reason: pick it to tidy each library path in place
instead of gathering files into one.

When Cove has **one** library path there is nothing to choose, so the picker is not shown and that
path is the root of every rule you make. _Where files go_ is the one exception: with its folder
template blank it names nowhere, which is what "rename in place" means, so it takes that path only
once you give it a folder to make.

That is true of the folder template here and of every [destination rule](#destination-routing) alike,
so there is nothing to combine: Renamer asks "where does this file go?" once, takes the first matching
rule, and renders **that** rule's destination. _Where files go_ is the default, used for an item no
rule matched — never appended to a rule's answer. If you used Renamer before this release the two
_were_ joined, a rule's root with the folder template rendered underneath; the one-time conversion
folded that template into each rule when you upgraded, so the rule you are looking at already
contains it and there is nothing to add back.

#### Example

With Cove library paths `/media/library` and `/media/archive`:

| Under                           | Folder template      | An item in `/media/library/2024/incoming` goes to |
| ------------------------------- | -------------------- | ------------------------------------------------- |
| _(the file's own library path)_ | `$performers`        | `/media/library/Ann Miller/`                      |
| _(the file's own library path)_ | `videos/$performers` | `/media/library/videos/Ann Miller/`               |
| `/media/archive`                | `$performers`        | `/media/archive/Ann Miller/`                      |
| _(the file's own library path)_ | _(blank)_            | nowhere — it is renamed where it is               |

Running the rename again leaves the file exactly where the first run put it, because the root does not
move when the file does.

#### Keeping an intermediate level

If your files live at `/media/library/videos/<studio>/` and you want them to stay under `videos`, put
that level in the **template**: `videos/$studio`. The root stays _(the file's own library path)_.

#### Why you cannot type a path

Cove already owns your library paths. A typed copy of one would point at nothing the moment you
changed it in Cove; a root picked from the list is a reference that follows it.

#### When you change Cove's library paths

Renamer is handed the **current list** of library paths every time it plans, never a record of how you
edited it. That one fact decides all three cases:

- **You add a path.** Every rule keeps its own root, so nothing a rule matches changes, and the files
  the new path brings into Cove are renamed like any others. The one thing to watch is a destination
  set to _(the file's own library path)_, which measures from the **innermost** library path holding
  the file: adding a path _inside_ one you already have moves those items down into it —
  `/media/library/Ann Miller/` becomes `/media/library/videos/Ann Miller/` if you add
  `/media/library/videos`. Adding a path _above_ one leaves the items already there exactly where they
  are.
- **You remove a path a destination points at.** Those items are **skipped and left alone**, and the
  dry run names the rule and the missing root. They are never quietly handed to _Where files go_ —
  that would move files somewhere you did not choose because a rule broke. Re-pick the root, or add the
  folder back to Cove.
- **You rename a path.** Renamer cannot tell a rename from a removal and an addition, because it sees
  a list rather than an edit. So a rule pointed at the old name behaves exactly as the case above:
  skipped until you re-pick it. This holds even when the new name still contains the old folder —
  broadening `/media/library/videos` to `/media/library` leaves the rule's root inside a library path
  but no longer **one of** them, and Renamer will not guess that you meant the same place.

#### When a destination cannot be worked out

| What happened                                                           | What Renamer does                                                                                                                        |
| ----------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| The root you picked is no longer one of Cove's library paths            | _Skipped — destination root no longer exists._ Re-pick it. The items do **not** fall back to the default.                                |
| _(the file's own library path)_ and the file is under none of them      | _Skipped — outside every Cove library path._ Add its folder to Cove's library paths, or pick a library path for the destination instead. |
| The destination is inside your library but outside every _Allowed root_ | _Skipped — destination is not under any allowed root._ Add that library path under _Allowed roots_, or clear _Allowed roots_.            |

None of these fails the run, and none of them touches the file. The first two differ in one way worth
knowing: the second is about the folder, so those items are still renamed normally when no folder
template is set — the first is about the destination itself, so a rule whose root has gone skips its
items whether or not a folder would have been made.

#### When a rename lands outside your Cove library

The dry run marks a row **Lands outside your Cove library** when the file is renamed where it already
sits and that folder is under none of Cove's library paths. This is a warning, not a skip: the rename
**still happens exactly as previewed**, and nothing about the new name or folder changes. What it costs
is that Cove will not see the file at its new location — a rescan never re-examines it, and if anything
later moves it on disk Cove cannot rediscover it.

A rule's own destination never lands here. Every destination measures from a Cove library path, so a
rule that still resolves writes inside your library, and a rule whose root has gone is skipped by the
table above instead.

## What gets renamed

| Setting                                | What it does                                                                                                                                            | Default |
| -------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- | ------- |
| Only rename organized items            | Skip items whose _Organized_ flag is off, so un-curated items don't get names. (A configured _Unorganized destination_ overrides this for those items.) | Off     |
| Use filename as title when none is set | When an item has no title, work one out from its filename and **save it to the item's Title**. See below.                                               | Off     |
| Required fields                        | Token names that must resolve to a non-empty value, or the item is skipped. Empty = no gate.                                                            | `title` |

### Use filename as title when none is set

This is the only setting that changes an item's **metadata**. Every other setting changes a file's
name or its location; this one writes to Cove's `Title` field.

Turn it on and an item with no title gets one from its first file's filename, minus the extension, at
the moment Renamer renames it. The title is saved together with the rename, in one operation. From
then on the item has a title like any other, and Renamer reads it rather than working it out again.

- A title you have already set is **never** overwritten, and neither is one you set later.
- An item Renamer does not rename keeps no title — nothing is written when nothing changes.
- [Undo](./guide#undo-the-last-rename) puts the file back at its old name and folder. It leaves the
  title, because by then the title is your metadata.

With this off (the default), an item with no title resolves `$title` to nothing, and the shipped
`title` required field then skips it. That skip is the safe outcome for an item Renamer knows nothing
about, and the dry run says so.

## Run & automation

| Setting               | What it does                                                                                | Default |
| --------------------- | ------------------------------------------------------------------------------------------- | ------- |
| Auto-rename on update | Re-rename an item automatically when Cove raises a `video.updated` / `image.updated` event. | Off     |

Auto-rename can only act on an event Cove actually raises. On **Cove 1.1.0 and earlier, editing
several items at once does not raise those events at all** — the host saves the rows and stays
silent, so nothing is renamed, and no error appears anywhere. Editing one item at a time still
works, and so do **Dry run** and **Rename all files**, which do not depend on events. Cove fixed
this after 1.1.0; on a newer host a multi-item edit renames every item it selected.

This section also holds the **Dry run** and **Rename all files** actions (see the
[User guide](./guide#preview-with-a-dry-run)). Those run a rename; they aren't saved settings.

## Token settings

These cards appear only when your template uses the matching token.

### Performers (`$performers`) and Tags (`$tags`)

Both are multi-value lists shaped by the same options (a few apply to performers only):

| Setting                            | What it does                                                                                                                                                                | Default         |
| ---------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------- |
| Separator                          | Text inserted between joined items.                                                                                                                                         | `" "` (a space) |
| Max count                          | Maximum items to include; `0` = unlimited.                                                                                                                                  | `0`             |
| On overflow                        | When over the max: _Drop all_ or _Keep the first N_.                                                                                                                        | Drop all        |
| Sort                               | Order before joining: Name (A→Z), Keep original order, and — performers only — By internal id, Favorites first then name.                                                   | Name (A→Z)      |
| Whitelist                          | If non-empty, only the listed performers or tags are kept. Each is matched on its stable id in your library, not on its name, so renaming one keeps the rule pointed at it. | _(empty)_       |
| Blacklist                          | The listed performers or tags are removed. Matched the same way as the whitelist.                                                                                           | _(empty)_       |
| Ignore genders _(performers only)_ | Genders to drop before the max-count limit. A performer with no gender set is always kept.                                                                                  | _(empty)_       |
| Gender order _(performers only)_   | Preferred gender order, most-preferred first; controls who survives the max-count limit.                                                                                    | _(empty)_       |

### Date & duration format

| Setting         | What it does                                                                                                  | Default      |
| --------------- | ------------------------------------------------------------------------------------------------------------- | ------------ |
| Date format     | .NET date format for `$date`. Options include `yyyy-MM-dd`, `yyyy`, `MM-dd-yyyy`, `dd.MM.yyyy`, `yyyy.MM.dd`. | `yyyy-MM-dd` |
| Duration format | .NET duration format for `$duration`, e.g. `hh-mm-ss`, `hh.mm.ss`, `mm-ss`.                                   | `hh-mm-ss`   |

## Destination routing

Renamer decides where each item goes by checking rules in a fixed **precedence order**:

> **Excludes → Unorganized → Tag → Studio (including parent studios) → Source path**

Within a category the first matching rule (in your order) wins; excludes always run first. The
order of the cards below in the UI is for convenience and does not change this precedence. **Rules
match on a tag, a studio or a source path — there is no per-performer destination.** `$performers`
shapes names and folder templates, and nothing routes on it.

An item that matches no rule at all follows **_Where files go_** (Filename & destination), which is the
default and the last step of the cascade rather than a step of its own. With its folder template blank
that means the item is renamed where it already sits.

Two consequences of the order that surprise people, both correct:

- **_Unorganized_ outranks everything except excludes**, so an un-curated item goes to the unorganized
  destination even when a studio or tag rule matches it, and even when _Where files go_ would have put
  it somewhere else. Curate the item — or leave the unorganized destination blank — if you want its
  other rules to decide.
- **A token that renders empty does not disqualify a rule.** An unorganized item with no studio and an
  unorganized folder template of `$studio` lands at the root of that destination, with the empty level
  dropped rather than made as a folder called nothing.

### Unorganized destination

| Setting                 | What it does                                                                                                                                  | Default   |
| ----------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- | --------- |
| Unorganized destination | The route for items whose _Organized_ flag is off (resolved before tag/studio/path). Overrides _Only rename organized items_ for those items. | _(blank)_ |

### Per-studio and per-tag destinations

| Setting                 | What it does                                                                                                                                                 | Default  |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ | -------- |
| Per-studio destinations | Map a studio → a destination (a library root + a folder template). Keyed on the studio's stable id, so a name typo never splits one studio across two trees. | _(none)_ |
| Per-tag destinations    | Map a tag → a destination. Keyed on the tag's stable id, so renaming a tag keeps its rule pointed at it.                                                     | _(none)_ |

An un-curated item goes to the [unorganized destination](#unorganized-destination) instead, whatever
studio or tag rule matches it — see [precedence](#destination-routing).

#### A studio rule reaches that studio's children

A per-studio rule applies to the studio you set it on **and to every child studio under it that has
no rule of its own**. A child's own rule always wins over an inherited one — direct beats ancestor.

Say Cove holds this, and you set a rule only on the parent:

| Studio                    | Parent       | Own rule?              | Destination it uses |
| ------------------------- | ------------ | ---------------------- | ------------------- |
| `ExploitedX`              | —            | yes → `I:\Downloads\P` | `I:\Downloads\P`    |
| `Exploited College Girls` | `ExploitedX` | yes → `I:\Downloads\P` | its own             |
| `Backroom Casting Couch`  | `ExploitedX` | **no**                 | **inherited**       |
| `ExCoGigirls`             | `ExploitedX` | **no**                 | **inherited**       |

The rule-less children all follow `ExploitedX`. This is usually what you want — it is how one rule
covers a whole studio family — but it means **a rule on a parent studio can move files belonging to
children you were not thinking about**, including across drives.

Before you save a rule on a parent studio, check which children sit under it. Then run a dry run and
read the destination column. To keep one child where it is, give that child its own rule.

### Advanced routing & safety

| Setting                  | What it does                                                                                                                                                                                           | Default   |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | --------- |
| Allowed roots            | **Narrows** where a rename may write to these absolute folders. Every destination is already inside a Cove library path, so this can only make that area smaller — never larger. Empty = no narrowing. | _(empty)_ |
| Source-path destinations | Ordered rules matching an item's source path (exact, or a regex) → a destination. Exact matches are tried before regex.                                                                                | _(none)_  |

A source-path rule matches on where a file **is**, so it stops matching once it has moved the item.
On the next run that item matches no rule and follows **_Where files go_** instead. A source-path rule
together with a _Where files go_ that names a folder therefore relocates an item twice: the first run
sends it to the rule's destination, and the next run sends it to the default one, where it then stays.

### Sidecar files and empty folders

| Setting                                              | What it does                                                                                                                                                       | Default   |
| ---------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ | --------- |
| Also move sidecar files with these extensions        | Extensions whose same-name neighbor file moves alongside the primary (e.g. `srt` for subtitles). An [undo](./guide#undo-the-last-rename) brings them back with it. | _(empty)_ |
| Delete the source folder when a move leaves it empty | After a move empties the source folder, delete it (only-if-empty, non-recursive). Undo will not re-create it.                                                      | Off       |

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

| Setting                 | What it does                                                                                                                              | Default                                                                                     |
| ----------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| Filename max length     | Maximum length of the filename.                                                                                                           | `255`                                                                                       |
| Full-path max length    | Maximum length of the full path. Measured against the name the template renders; a name that gains a duplicate suffix is not re-measured. | `259`                                                                                       |
| Drop order              | When a name is too long, the order in which fields are dropped to fit (first listed dropped first).                                       | `videoCodec, audioCodec, frameRate, resolution, tags, studioCode, studio, performers, date` |
| Duplicate suffix format | Suffix added before the extension when the target name is taken; `{n}` is the collision counter.                                          | `" ({n})"` → `name (1).mp4`                                                                 |

### Cross-drive concurrency

| Setting                  | What it does                                                                                                                                                                                                         | Default |
| ------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------- |
| Cross-volume concurrency | How many files to copy across drives at once. Leave at 2 for regular hard drives; raise to 4–8 if both drives are SSDs. Higher is not always faster — on spinning disks it can be slower. Clamped to 1–16 in the UI. | `2`     |
| Same-volume concurrency  | How many same-drive renames to run at once (these are instant; the default is fine). Clamped to 1–16 in the UI.                                                                                                      | `8`     |

### Excludes

| Setting                | What it does                                                                                                        | Default   |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------- | --------- |
| Exclude by tag         | Items carrying any of these tags are excluded from renaming/moving (evaluated first). Keyed on the tag's stable id. | _(empty)_ |
| Exclude by studio      | Items whose studio (or a parent studio) matches are excluded. Keyed on stable studio id.                            | _(empty)_ |
| Exclude by source path | Ordered exact-or-regex source-path rules; a match excludes the item.                                                | _(empty)_ |

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
