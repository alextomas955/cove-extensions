---
id: templates
title: Naming templates
sidebar_position: 3
---

A naming template is the pattern Renamer uses to build each new filename (and, optionally, folder
path). It is plain text mixed with `$tokens` that Cove replaces with each item's metadata.

## A worked example

The shipped default template is:

```text
{$date - }$title{ [$resolution]}
```

For a 1080p video titled *The Matrix* dated 1999-03-31, it produces:

```text
1999-03-31 - The Matrix [1080p].mp4
```

The `{ … }` groups mean the surrounding punctuation only appears when the token inside has a value.
The same template degrades cleanly as metadata gets sparser:

| Item metadata | Rendered filename |
| --- | --- |
| title + date + resolution | `1999-03-31 - The Matrix [1080p].mp4` |
| title + resolution (no date) | `The Matrix [1080p].mp4` |
| title only | `The Matrix.mp4` |

The extension is always added automatically — you don't put `$ext` in the template unless you want
to place it somewhere unusual.

## Syntax

- **Tokens** are written bare with a leading `$`: `$title`, `$resolution`, `$studio`. There is no
  `${title}` form.
- **Optional groups** use braces: `{ … }`. Everything inside a group — including its leading
  separator and literal punctuation — disappears when **every** token inside the group is empty.
  Put the separator *inside* the group (`$title{ - $studio}`, not `$title - {$studio}`) so you never
  get a dangling ` - ` when the studio is missing.
- **A literal dollar sign** is written `$$`.
- **Missing tokens are omitted**, not rendered as blank — which is what makes the `{ … }` groups
  collapse.
- **Folders**: in the folder template, use `/` to separate sub-folders, e.g. `$studio / $year`.

## Presets

The settings panel offers these one-click starter templates. Pick one, then edit from there:

| Preset | Template |
| --- | --- |
| Date – Title [Resolution] *(default)* | `{$date - }$title{ [$resolution]}` |
| Title + resolution | `$title{ [$resolution]}` |
| Studio – Title [Res] | `$studio{ - $title}{ [$resolution]}` |
| Date – Title | `$date{ - $title}` |
| Performers – Title | `$performers{ - $title}` |

Presets set only the filename template; the folder template stays as you left it (folder moves are
opt-in).

## Token reference

Each token below is shown with the kind of value it produces. Media tokens only appear for files
that carry them (a video has codecs and a frame rate; an image or audio file may not), and any token
with no value is simply omitted.

### Core

| Token | Produces | Example |
| --- | --- | --- |
| `$title` | The item's title. If the item has no title and *Use filename as title* is on, the file's current basename (without extension). | `The Matrix` |
| `$ext` | The file extension (added automatically at the end; include it explicitly only for unusual placements). | `mp4` |

### Titles, studios, people

| Token | Produces | Example |
| --- | --- | --- |
| `$studio` | The studio name. | `Studio Ghibli` |
| `$parentStudio` | The nearest parent studio's name. | `Toho` |
| `$studioCode` | The studio's code. | `SG-042` |
| `$director` | The director (videos only). | `Lana Wachowski` |
| `$performers` | The performer names, joined and shaped by the **Performers** token settings. | `Keanu Reeves Carrie-Anne Moss` |
| `$tags` | The tag names, joined and shaped by the **Tags** token settings. | `Sci-Fi Action` |

### Date & time

| Token | Produces | Example |
| --- | --- | --- |
| `$date` | The item's date, formatted by the **Date format** setting. | `1999-03-31` |
| `$year` | The calendar year of the item's date. | `1999` |
| `$duration` | The file's duration, formatted by the **Duration format** setting. | `02-16-00` |

### Media info

| Token | Produces | Example |
| --- | --- | --- |
| `$resolution` | A resolution label derived from the height (see below). | `1080p` |
| `$height` | Frame height in pixels. | `1080` |
| `$width` | Frame width in pixels. | `1920` |
| `$videoCodec` | The video codec. | `h264` |
| `$audioCodec` | The audio codec. | `aac` |
| `$frameRate` | The frame rate. | `23.976` |
| `$bitrate` | The file's bitrate in kbps. | `4500` |

#### Resolution labels

`$resolution` maps the frame height to a friendly label:

| Height (px) | `$resolution` |
| --- | --- |
| ≥ 2160 | `4k` |
| ≥ 1440 | `1440p` |
| ≥ 1080 | `1080p` |
| ≥ 720 | `720p` |
| ≥ 480 | `480p` |
| below 480 | the raw height as a number |

If a title already ends with a resolution label (for example `My Movie [1080p]`) and your template
also renders `$resolution`, Renamer removes the duplicate from the title so the label isn't repeated.

## Shaping multi-value tokens

`$performers` and `$tags` are lists. How they join into the name — the separator between items, a
maximum count, sort order, and include/exclude lists — is controlled by the **Performers** and
**Tags** cards under **Token settings**, which appear only when your template uses that token. See
the [Settings reference](./settings#token-settings) for every option.
