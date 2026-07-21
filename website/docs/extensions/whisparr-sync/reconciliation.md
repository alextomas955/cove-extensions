---
id: reconciliation
title: View the reconciliation
---

The reconciliation view compares what Whisparr tracks against what your Cove library holds and shows
you the result — matched, unmatched, and needs-review — without changing anything in Cove or
Whisparr. Use it to check identity matching, and to confirm or
reject the ambiguous matches yourself.

You need a connected Whisparr instance first (see the [Connect guide](./guide)).

## Open the reconciliation

Go to **Settings → Extensions → Whisparr Sync**. The reconciliation section is below the connection
section on the same page.

## Refresh

Click **Refresh reconciliation**. The extension reads your Cove library and the live Whisparr movie
list and shows every scene with how it lines up. Nothing is written to Cove or Whisparr — this is a
read-only comparison you can run as often as you like.

The summary line reads `{N} matched · {M} unmatched · {K} need review · {T} scenes`.

![The read-only reconciliation section on the Whisparr Sync settings page. Once you run a reconcile it lists scenes with their match state, match method, and Cove item, filtered by the segment chips above it.](/img/whisparr-sync/reconciliation.png)

*The read-only reconciliation section, shown against a synthetic fixture library — no real media.*

## Read the segments

Filter the table with the chips above it. A segment with no rows is shown but disabled.

- **Matched** — a confident link (a shared StashDB id, or a shared ThePornDB id), or one you confirmed.
- **Needs review** — an id shared by more than one Cove video, waiting for your decision. These are
  never applied on their own.
- **Unmatched** — no confident link. This is the safe default when nothing lines up.

The **Match method** column shows which check resolved a row: **StashDB id** or **ThePornDB id**.
The **Cove item** column links to the matched item in Cove; a row with no match reads `— no match`.

Sort by any column header, and search by scene title, Cove title, or match method.

## Confirm or reject a needs-review match

On a **Needs review** row:

- Click **Confirm** to accept the suggestion. The row becomes Matched and the link is remembered on
  the next refresh.
- Click **Reject** to decline it. The suggestion won't be offered automatically again.

Confirm and Reject write only to the extension's own match store — never to your Cove library or to
Whisparr — and are reversible: the next **Refresh reconciliation** recomputes everything from scratch.

## Matching on Whisparr v2

On **Whisparr v2**, reconciliation matches by the **ThePornDB id** each scene already carries — the
same id-only rule as v3, just keyed on a different id. Whisparr v2 carries no StashDB id on any scene,
so the StashDB-id check simply never fires for a v2 row; matching relies entirely on the ThePornDB id
instead.

A v2 scene with no ThePornDB id yet (not identified in Cove) shows as unmatched until Cove's Identify
feature attaches one, exactly like an unidentified v3 scene. Everything else — connect, import,
confirm/reject — behaves the same as on v3.
