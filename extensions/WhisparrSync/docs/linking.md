---
id: linking
title: Link the files you already own
sidebar_position: 3
---

Whisparr holds one entry per folder, and your library is arranged the way you arrange it. So Cove
gives each file you own a second name in a folder it keeps for that scene or studio, and tells
Whisparr the entry lives there. Nothing is copied, nothing of yours is renamed, and none of your
files move.

This runs when you press **Reflect owned** on a studio or a performer, when you turn monitoring on,
and as part of a library run from the **Whisparr Sync** settings tab.

## Two Whisparr settings, and what they govern

Most of this hands the files to Whisparr to take in, and two of Whisparr's own settings decide
whether it can. Turn them the right way before you press **Reflect owned**, before you turn
monitoring on, and before a library run on Whisparr v3 (Eros). Both live in Whisparr, under
**Settings → Media Management**.

1. Turn **Rename Movies** and **Rename Scenes** off, on Whisparr v3 (Eros), in the **Movie Naming**
   section. On Whisparr v2 the one switch is **Rename Episodes**, in the **Episode Naming** section.
2. Leave **Use Hardlinks instead of Copy** on. It is on in a fresh Whisparr.

With renaming on, Whisparr moves what it takes in out of the folder it was handed, so Cove sends it
nothing and the run says: _No files were handed to Whisparr: it is set to rename files. Turn
renaming off in Whisparr's Settings, Media Management._ A press of **Reflect owned** says the same
above the control.

With the hard-link setting off, every file would be copied in full and use your disk twice, so Cove
sends nothing there either and the run says: _No files were handed to Whisparr: its hard-link
setting is off._ A setting Cove cannot read at all stops the run the same way and names which of the
two it could not read.

**The second names are made whatever these settings say.** Cove makes them itself, so nothing is
copied and neither setting stops them. What the settings govern is handing the folder to Whisparr
afterwards. A run with either setting against it still builds the folders and still states how many
second names it made, and it hands none of them over.

## What Whisparr shows you now

Whisparr's own file list shows every file you own at a path inside one of these folders, not at the
path Cove shows you. Two things follow, and both are worth knowing before you go looking.

**Deleting a file from disk from inside Whisparr does not delete your file.** It removes the second
name, so your own copy stays where it is and Cove still holds it. The next run gives that scene its
second name back.

**The names in there are not your names.** Each one is made from the file's own identity, so it
looks like `38-200000007e4de.mp4`. That is deliberate: a file's identity does not change when you
rename it, so renaming your own file never makes a second copy appear in Whisparr.

## What appears in your library folders

At the top of each library folder that holds files being linked, Cove creates one folder:
`.wsync-v3` for Whisparr v3 (Eros), or `.wsync-v2` for Whisparr v2. Inside it is one folder per
entry Whisparr holds, named for the identifier Whisparr knows that entry by, and inside each of
those is one second name per file you own for it.

Those second names cost no disk. A second name for a file is not a second copy: the same bytes
answer to both names, and the space comes back when the last name goes.

Cove's own library scan never looks inside it. The folder carries a `.coveignore` file whose one
line is `*`, which is how Cove is told to skip everything below a folder, so the second names never
turn up in your library as duplicates.

**Whisparr builds folders in there too.** Once an entry is registered inside `.wsync-v3` or
`.wsync-v2`, anything Whisparr imports for that entry lands in a folder Whisparr composes for itself
under the same place, named its own way. So folders will appear in there that Cove did not make.

## When you rename, move or delete a file

- **You rename it, or move it inside the same drive.** Nothing to do. The second name is a name for
  the same file, so it follows the file wherever it goes on that drive.
- **You move it to another drive.** A second name cannot cross a drive. The next run builds the
  second name on the new drive, moves Whisparr's entry to the folder there, and takes the name on
  the old drive back, which frees the bytes it was still holding.
- **You delete it.** The next run takes the second name back, and the space comes back with it.

## A file Cove did not put there

A run's closing line sometimes says: _Some files in the folders Whisparr was given were not put
there by Cove, so they were left alone._

That is a file Whisparr downloaded into one of these folders and Cove could not place in your
library. It is still where Whisparr put it. Cove never removes it, however long it sits there, and
never removes anything else it did not write. Move it into your library yourself, and Cove's next
scan will pick it up. The **Whisparr Sync** settings tab reports these too, as _Cove could not give
this downloaded file a place in your library, so it was left where Whisparr put it._

## What a run tells you

A finished run states two figures, and they count different things:

- **linked** is how many second names Cove made.
- **recorded by Whisparr** is how many files Whisparr now holds against an entry of its own.

They differ on purpose. A run can make every second name and hand them all over, and Whisparr's own
queue can still be minutes behind after it re-reads its catalogue. The figures are about what Cove
sent; Whisparr's own screens are where you see what it has taken in.

A studio Whisparr has not finished reading its catalogue for holds no entry to record a file
against, so that studio's files are handed over on a later run instead. Registering a studio starts
that read, and on a large studio it runs for hours, so the first run after you register one usually
records less than it linked.

## Starting the run that finishes the job

Press **Count what would sync** again once the catalogues have loaded, and the **Of those, with no
file recorded** row says how many entries Whisparr holds no file for. While that row is above zero
the **Sync library to Whisparr** button stays available, even when there is nothing left to
register, and its confirmation names the figure. Press it, and the run hands those files over.

Whisparr's catalogue read takes as long as it takes, so a second run may still find some
outstanding. Count again later and run again.

## Where it stops short

**A studio whose files sit on two drives is registered on one of them.** Whisparr holds one folder
per entry and a folder is on one drive. Cove registers the entry on the drive most of that studio's
files are on, leaves the files on the other drive where they are, and says so: _Some files were not
linked: they are not on the drive Cove keeps their entity's folder on, and nothing was copied._

**Cove has to be able to write inside your library folders.** Where it cannot, it builds no folder
and links nothing there, and the run says which library folder it was: _Nothing under \<folder\> was
given a folder of its own: Cove could not write inside that library path._

**On Linux, Cove also has to be able to write the file it is linking.** The kernel refuses a second
name for a file the linking user neither owns nor may write. Your own library files are Cove's to
write, so this does not touch them. A file Whisparr downloaded belongs to Whisparr's user, which is
the ordinary arrangement when Cove and Whisparr run in separate containers, and Cove can only give
it a second name if the two share a user or a group with write access to it. Sharing the folder is
not enough.
