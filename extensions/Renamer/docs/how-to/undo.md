---
id: undo
title: Undo a rename
sidebar_position: 6
description: Put the files from your last rename back to their old names and folders.
---

Renamer records each rename so you can put the files back for 7 days afterwards.

## Undo the last rename

1. Scroll to the foot of the Renamer page. It shows your last rename: how many items, how long ago,
   and the date its undo window closes.
2. Select **Undo last rename**.

   ![The foot of the Renamer page, showing the last rename and the Undo last rename button.](../img/undo-footer.jpg)

3. Check the number of files in the confirmation, then select **Undo _N_ renames**.

   ![The Undo last rename confirmation, which says how many files it will move back.](../img/undo-confirm.jpg)

Renamer moves the files back to their old names and folders, and updates Cove to match. Same-name
companion files, such as a `.srt` subtitle, and the captions Cove tracks for the item come back too.

![The result of an undo: 6 files moved back to their original names.](../img/undo-done.jpg)

The **Undo** link in the message after a rename takes you to the same place.

## Good to know

- **Undo lasts 7 days.** After that the line reads "undo expired" and the button is no longer offered.
- **One undo covers a whole run.** A **Rename all files** run that touched videos and images is put
  back in one go. You need write permission for every kind it touched.
- **Undo is shared.** It offers the most recent rename that anyone on your Cove made. If other people
  use your Cove, check the line above the button first.
- **Undo reaches the most recent rename only.** If several renames are waiting, you cannot skip past
  the newest to reach an older one.
- **A file that can't go back yet stays pending.** If something now holds the old name, or a drive is
  unplugged, fix that and select **Undo last rename** again. Only what is left is moved.
- **Undo can't restore names from before Renamer.** It only reverses Renamer's own renames. To change
  your mind about a template later, pick a new one and rename again.

For the rare cases where undo cannot put everything back, see
[Troubleshooting](../troubleshooting#undo).
