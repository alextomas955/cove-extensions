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

Open **Settings**, then under **Extensions** select **Scan and Identify**. The panel reports the Cove version it loaded against
and whether the host services it needs are available. It has no run controls yet.
