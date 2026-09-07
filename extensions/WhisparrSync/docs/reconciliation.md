---
id: reconciliation
title: Reconciliation
---

Reconciliation is the backstop for auto-import. It makes sure a file Whisparr finished importing still
reaches your Cove library when the webhook that should have announced it never arrived.

It exists because a webhook can be missed — Cove restarting, a network blip, a connection removed in
Whisparr. Reconciliation is the backstop that notices what the webhook did not.

## What runs, and when

Every 15 minutes the extension reads Whisparr's import history since the last checkpoint it stored and
feeds anything it hasn't seen through the same import path the webhook uses. That is the whole job: an
**ingest backstop**, not a survey of your library.

It brings a file into your library **in place** — it never moves, renames, or deletes anything on disk,
and it changes nothing in Whisparr. Two properties keep it safe to leave running:

- It is an **exclusive** job, so two passes never overlap.
- It is **idempotent**. An import that arrives on both channels is ingested once, because the webhook and
  the poll derive the same key for the same import.

The first pass after you connect only records where Whisparr's history currently ends; it imports
nothing. History from before you set the extension up is never replayed into your library.

## Where you see it

There is no reconciliation page, no table, and no button to press. You see the effect instead: files
Whisparr acquired turn up in your library. The settings page reports the pipeline's health rather than a
per-attempt list: the **Import webhook** section shows when the last event arrived, and a warning appears
above it if recent imports landed at a path Cove couldn't open.

Nothing about a match is saved. The surfaces that answer *which Whisparr scene is this?* — the per-scene
[Whisparr status](./status.md) views — derive their answer live from Whisparr's own list plus your
library on every read, so what you see is always current rather than a verdict left over from an earlier
pass.

## How a scene is matched

Matching a Cove item to a Whisparr scene is **id-only**. The extension links the two when they share the
one remote id both systems already key on, and otherwise leaves them unlinked:

- **Whisparr v3 (Eros)** — the **StashDB** id.
- **Whisparr v2** — the **ThePornDB** id.

There is no title, date, or filename guessing. An id match is either there or it is not, which is what
makes the answer safe to act on without asking you to approve each one.

## Matching on Whisparr v2

On **Whisparr v2**, the match is the **ThePornDB id** each scene already carries — the same id-only rule as
v3, just keyed on a different id. Whisparr v2 carries no StashDB id on any scene, so the StashDB check
simply never fires for a v2 row; matching relies entirely on the ThePornDB id.

A v2 scene with no ThePornDB id yet (not identified in Cove) stays unmatched until Cove's Identify feature
attaches one, exactly like an unidentified v3 scene. Everything else — connect, import, status — behaves
the same as on v3.
