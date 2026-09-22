---
id: templates
title: Naming templates
sidebar_position: 4
---

A naming template is the pattern Renamer uses to build each new filename (and, optionally, folder
path). It is plain text mixed with `$tokens` that Cove replaces with each item's metadata.

## A worked example

The shipped default template is:

```text
{$date - }$title{ [$resolution]}
```

For a 1080p video titled _The Matrix_ dated 1999-03-31, it produces:

```text
1999-03-31 - The Matrix [1080p].mp4
```

The `{ … }` groups mean the surrounding punctuation only appears when the token inside has a value.
The same template degrades cleanly as metadata gets sparser:

| Item metadata                | Rendered filename                     |
| ---------------------------- | ------------------------------------- |
| title + date + resolution    | `1999-03-31 - The Matrix [1080p].mp4` |
| title + resolution (no date) | `The Matrix [1080p].mp4`              |
| title only                   | `The Matrix.mp4`                      |

The extension is always added automatically, at the end. Leave `$ext` out of a filename template:
writing it changes nothing, because the renderer resolves it to nothing there and appends the real
extension afterwards either way. It cannot be moved.

## Syntax

- **Tokens** are written bare with a leading `$`: `$title`, `$resolution`, `$studio`. There is no
  `${title}` form.
- **Optional groups** use braces: `{ … }`. Everything inside a group — including its leading
  separator and literal punctuation — disappears when **every** token inside the group is empty.
  Put the separator _inside_ the group (`$title{ - $studio}`, not `$title - {$studio}`) so you never
  get a dangling `-` when the studio is missing.
- **A literal dollar sign** is written `$$`.
- **Missing tokens are omitted**, not rendered as blank — which is what makes the `{ … }` groups
  collapse.
- **Folders**: in a folder template, use `/` to separate sub-folders, e.g. `$studio/$year`. Only `/`
  separates; a backslash is treated as a character the filesystem forbids and is removed (or
  replaced, per _Illegal characters_). A folder template is always relative to its
  destination's root, so it never starts with a drive or a `/`. A level whose tokens all render empty
  is dropped rather than made as a folder with no name.

## Presets

The settings panel offers these one-click starter templates. Pick one, then edit from there:

| Preset                                | Template                             |
| ------------------------------------- | ------------------------------------ |
| Date – Title [Resolution] _(default)_ | `{$date - }$title{ [$resolution]}`   |
| Title + resolution                    | `$title{ [$resolution]}`             |
| Studio – Title [Res]                  | `$studio{ - $title}{ [$resolution]}` |
| Date – Title                          | `$date{ - $title}`                   |
| Performers – Title                    | `$performers{ - $title}`             |

Presets set only the filename template; the folder template stays as you left it (folder moves are
opt-in).

## Token reference

Each token below is shown with the kind of value it produces. Media tokens only appear for files
that carry them (a video has codecs and a frame rate; an image or audio file may not), and any token
with no value is simply omitted.

### Core

| Token    | Produces                                                                                                                                                                 | Example      |
| -------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------ |
| `$title` | The item's title. If the item has no title and _Use filename as title_ is on, the item's first filename (without extension), which is then recorded as the item's title. | `The Matrix` |
| `$ext`   | The file extension. Appended automatically at the end of a filename; writing `$ext` in a filename template resolves to nothing and does not move it.                     | `mp4`        |

### Titles, studios, people

| Token           | Produces                                                                     | Example                         |
| --------------- | ---------------------------------------------------------------------------- | ------------------------------- |
| `$studio`       | The studio name.                                                             | `Studio Ghibli`                 |
| `$parentStudio` | The nearest parent studio's name.                                            | `Toho`                          |
| `$studioCode`   | The studio's code.                                                           | `SG-042`                        |
| `$director`     | The director (videos only).                                                  | `Lana Wachowski`                |
| `$performers`   | The performer names, joined and shaped by the **Performers** token settings. | `Keanu Reeves Carrie-Anne Moss` |
| `$tags`         | The tag names, joined and shaped by the **Tags** token settings.             | `Sci-Fi Action`                 |

### Date & time

| Token       | Produces                                                               | Example      |
| ----------- | ---------------------------------------------------------------------- | ------------ |
| `$date`     | The item's date, formatted by the **Date format** setting.             | `1999-03-31` |
| `$year`     | The calendar year of the item's date.                                  | `1999`       |
| `$duration` | The file's duration, rendered through the **Duration format** setting. | `02-16-00`   |

### Media info

| Token         | Produces                                                    | Example  |
| ------------- | ----------------------------------------------------------- | -------- |
| `$resolution` | A resolution label derived from the frame size (see below). | `1080p`  |
| `$height`     | Frame height in pixels.                                     | `1080`   |
| `$width`      | Frame width in pixels.                                      | `1920`   |
| `$videoCodec` | The video codec.                                            | `h264`   |
| `$audioCodec` | The audio codec.                                            | `aac`    |
| `$frameRate`  | The frame rate.                                             | `23.976` |
| `$bitrate`    | The file's bitrate in kbps.                                 | `4500`   |

#### Resolution labels

`$resolution` is the same label Cove shows on the item itself, so the filename and the badge agree.

| Frame size  | `$resolution` |
| ----------- | ------------- |
| 256 x 144   | `144p`        |
| 426 x 240   | `240p`        |
| 640 x 360   | `360p`        |
| 854 x 480   | `480p`        |
| 960 x 540   | `540p`        |
| 1280 x 720  | `720p`        |
| 1920 x 1080 | `1080p`       |
| 2560 x 1440 | `1440p`       |
| 3840 x 2160 | `4K`          |
| 5120 x 2880 | `5K`          |
| 6144 x 3384 | `6K`          |
| 7168 x 4032 | `7K`          |
| 7680 x 4320 | `8K`          |
| 9840 x 4100 | `HUGE`        |

A frame that is not exactly one of these sizes takes the largest label it reaches, with about five
percent of slack: 1920 x 1200 is `1080p`, and so is 1830 x 1030.

Both edges count, so a portrait video gets the same label as the landscape video of the same
shape: 1080 x 1920 is `1080p`, not `1440p`. A very wide frame is labelled for its long edge rather
than its short one, so 2560 x 1080 is `1440p`.

A label needs both a width and a height, so a file Cove has no width stored for gets none. Neither
does a frame under 144 pixels on its longer edge and under about 137 on its shorter. The
`{ [$resolution]}` group in your template then drops whole, so the name carries no empty
brackets.

Renamer reads the label off the width and height Cove stored, so a per-token replacement rule on
`$width` or `$height` changes only that token in the name and leaves the label alone. A rule on
`$resolution` rewrites the label itself, and the name then reads differently from the badge.

If a title already ends with a resolution label (for example `My Movie [1080p]`) and your template
also renders `$resolution`, Renamer removes the duplicate from the title so the label isn't repeated.
Where the file has no width stored, or the frame is too small for any label, Renamer has no label to
write and the one already in your title stays. Where the name was too long and `$resolution` was
dropped to make it fit, the title's label goes with it, so the drop shortens the name instead of
lengthening it.

## Shaping multi-value tokens

`$performers` and `$tags` are lists. How they join into the name — the separator between items, a
maximum count, sort order, and the _Only include_ and _Never include_ lists — is controlled by the **Performers** and
**Tags** cards under **Token settings**, which appear only when your template uses that token. See
the [Settings reference](./settings#token-settings) for every option.
