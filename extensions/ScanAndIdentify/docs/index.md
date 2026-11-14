---
id: index
title: Scan and Identify
slug: /
sidebar_position: 1
description: Take a Cove library from newly imported files to tagged items in one run - scan, fingerprint, identify, then generate the images Cove shows.
---

Scan and Identify takes your Cove library from newly imported files to tagged items. Cove can already
do each step on its own, from a different screen, in an order you have to remember. This extension
runs them as one sequence and waits for each step to finish before it starts the next.

## What a run does

1. **Scan.** Cove looks through your library folders for files it does not know about yet, and works
   out the checksums and perceptual hashes it needs to tell two files apart.
2. **Fill in missing fingerprints.** Items that came in earlier without a fingerprint get one, so the
   identify step has something to match on.
3. **Identify.** Cove matches each item against its metadata sources and fills in the title, the
   studio, the performers, the date and the tags.
4. **Generate images.** Cove builds the covers, thumbnails, preview clips and sprites it shows for
   each item.

Steps run strictly in order, because each one needs what the one before it produced. Identifying an
item with no fingerprint finds nothing, and generating a cover for an item with no metadata gives you
a cover with nothing on it.

## What you need

- Cove 1.7.0 or later.
- A library folder registered in Cove, with read and write permission for the kinds of item you want
  identified.

## Where to find it

Open **Settings**, then under **Extensions** select **Scan and Identify**. The panel reports the Cove
version it loaded against and whether the host services it needs are available. Below that it
carries the **Run** card and the run options.

## Start a run

1. Open **Settings**, then under **Extensions** select **Scan and Identify**.
2. Check the switches under **Run options** are as you want them. A run reads them once, when it
   starts, so a switch you change afterwards does not affect the run going on.
3. In the **Run** card, select **Run**.

The four passes then run in order. Each one starts only after the one before it has finished, and
the card shows a row for each with where it has got to. **Run** stays unavailable until the run ends,
so you cannot start a second run on top of one that is going.

Identify goes through every item in the library, so a run over a whole library takes hours.

## Leave the page open

The run advances only while the **Scan and Identify** page is open. Closing the page, or moving to
another screen in Cove, leaves the pass that is working to finish on its own, and the passes after it
do not start. Open the page again and the run picks up from where it got to.

## What each pass can show

| State              | What it means                                                            |
| ------------------ | ------------------------------------------------------------------------ |
| **waiting**        | The pass has not started. It is waiting for the one before it.           |
| **starting**       | Cove has been asked for the pass and has not answered yet.               |
| **queued in Cove** | Cove has the work and is waiting for another job to finish first.        |
| **running**        | The pass is working.                                                     |
| **done**           | The pass finished.                                                       |
| **failed**         | The pass stopped on an error.                                            |
| **cancelled**      | The pass was stopped.                                                    |
| **skipped**        | The pass was turned off in the run options, so the run passed over it.   |
| **unknown**        | Cove no longer knows about the pass, so the run cannot tell how it went. |

With **Fill in missing fingerprints** off, the fingerprint pass reads **skipped** and identify starts
straight after the scan.

With all six switches under **Images and previews** off, the image pass reads **skipped** and the run
ends after identify.

## Stop a run

In the **Run** card, select **Cancel**. Cove stops the pass that is working and the run ends there.
The passes after it do not start.

Cove may take a short while to stop a pass that is already working, so the row can stay on
**running** for a moment after you select **Cancel**.

The card then says that you stopped the run, and names the pass it was on.

A run also stops by itself at the first pass that fails or that something else cancels. The passes
after it stay on **waiting** and never start. The card says why the run stopped and names the pass
it stopped at. A pass Cove cancelled reads as Cove's doing, which usually means Cove was shutting
down.

## What you can turn on and off

The panel saves a set of switches that a run reads. They are grouped in the order a run uses them.

Under **Scan**:

- **Force rescan (ignore mtime)**: look at every file again, even one whose timestamp has not
  changed since the last scan. Off by default, because a full rescan takes much longer.

Under **Fingerprints**:

- **Fill in missing fingerprints**: give a fingerprint to any item that came in without one. Turn it
  off for a library that already has a fingerprint on every file.

Under **Identify**:

- **Let Identify create new performers, studios and tags**: add a performer, studio or tag that your
  library does not hold yet, instead of leaving the match out.
- **Mark an identified video as organized**: set the organized flag on a video the run identified.
  Off by default.

Under **Images and previews**:

- **Thumbnails / screenshots**: the still images Cove shows for a video.
- **Video previews**: the short clip that plays when you hover over a video.
- **Sprite sheets**: the strip of frames the seek bar shows.
- **VR 3D covers and previews**: the stereoscopic cover and preview a headset shows. Off by default,
  because it costs a second encode for every VR video. The flat images come from the switches above.
- **Image thumbnails**: the small version of a still image.
- **Gallery cover thumbnails**: the cover Cove shows for a gallery.

A run never replaces an image or a preview that already exists, so running it again only fills the
gaps.

Select **Save** to store the switches. They stay as you set them after Cove restarts. Select
**Discard** to go back to what was last saved.
