---
id: reconciliation
title: View the reconciliation
---

The reconciliation view compares what Whisparr tracks against what your Cove library holds and shows
you the result — matched, unmatched, and needs-review — without changing anything in Cove or
Whisparr. Use it to check identity matching before any later phase acts on it, and to confirm or
reject the low-confidence matches yourself.

You need a connected Whisparr instance first (see the [Connect guide](./guide)).

## Open the reconciliation

Go to **Settings → Extensions → Whisparr Sync**. The reconciliation section is below the connection
section on the same page.

## Refresh

Click **Refresh reconciliation**. The extension reads your Cove library and the live Whisparr movie
list and shows every scene with how it lines up. Nothing is written to Cove or Whisparr — this is a
read-only comparison you can run as often as you like.

The summary line reads `{N} matched · {M} unmatched · {K} need review · {T} scenes`.

## Read the segments

Filter the table with the chips above it. A segment with no rows is shown but disabled.

- **Matched** — a confident link (a shared StashDB id, or an equal file path), or one you confirmed.
- **Needs review** — a low-confidence suggestion (a fuzzy title-and-year guess) waiting for your
  decision. These are never applied on their own.
- **Unmatched** — no confident link. This is the safe default when nothing lines up.

The **Match method** column shows which check resolved a row: **StashDB id**, **Path**, or **Fuzzy**.
The **Cove item** column links to the matched item in Cove; a row with no match reads `— no match`.

Sort by any column header, and search by scene title, Cove title, or match method.

## Confirm or reject a needs-review match

On a **Needs review** row:

- Click **Confirm** to accept the suggestion. The row becomes Matched and the link is remembered on
  the next refresh.
- Click **Reject** to decline it. The suggestion won't be offered automatically again.

Confirm and Reject write only to the extension's own match store — never to your Cove library or to
Whisparr — and are reversible: the next **Refresh reconciliation** recomputes everything from scratch.

## When paths don't line up

The path check only matches when Whisparr and Cove see a scene at the **same** path. If Whisparr runs
in a container and sees `/data/...` while Cove sees `/mnt/media/...`, the path check won't connect
them — matching then relies on the StashDB id (the authoritative key) or falls to a fuzzy suggestion.
A root-path translation map is not configured in this release.

## Matching on Whisparr v2

On **Whisparr v2**, reconciliation uses the **path** leg and the **fuzzy title/year** leg only — the
StashDB-id leg never fires. Whisparr v2 is ThePornDB-native and carries no StashDB id on any scene, so
there is no authoritative id to match on. This is a permanent property of v2's data model, not a
missing feature.

The practical effect: with no durable id to lead with, a v2 scene matches only when Whisparr and Cove
see it at the same path, or when you **Confirm** a fuzzy suggestion. Expect more v2 scenes in
**unmatched** or **needs review** than on v3, especially when Whisparr and Cove see files at different
paths. Everything else — connect, import, confirm/reject — behaves the same as on v3.
