---
id: index
title: Whisparr Sync
slug: /
---

Whisparr Sync connects Cove to a self-hosted [Whisparr](https://whisparr.com) v3 (Eros) or v2
instance. You enter your Whisparr URL and API key on a settings page, test the connection, set the
defaults Whisparr applies when it adds an item, and generate a webhook URL Whisparr can
call. Your API key is stored server-side only — it is never shown back to you and never written to
logs.

## In this section

- [Connect guide](./guide.md) — set up Whisparr and Cove for your version (a **v3 (Eros)** section and
  a **v2** section), test the connection, and add the webhook.
- [Monitor a studio or performer](./monitoring.md) — turn Whisparr monitoring on from an entity's Cove
  page and read its status line.
- [Find the scenes you're missing](./find-missing-scenes.md) — open an entity's **Missing** tab, narrow
  the list, and mark scenes wanted or grab them.
- [Missing tab reference](./discovery.md) — every part of the per-entity **Missing** tab: the scene
  cards, filters, bulk actions, states, and how the catalogue is sourced.
- [Wanted, queue & history](./activity.md) — a read-only page tracking what Whisparr is looking for,
  downloading now, and has already acquired.
- [Reconciliation](./reconciliation.md) — the 15-minute backstop that imports anything the webhook
  missed, and how a Cove item is matched to a Whisparr scene.
- [Whisparr status](./status.md) — the per-scene states (monitored / unmonitored / not added /
  excluded), how each is derived, and the three surfaces that show them.
- [Settings reference](./settings.md) — every setting on the page, with defaults and valid values.
- [Changelog](./changelog.md) — user-facing changes, newest first.

## What it does today

This release connects Cove to Whisparr and keeps the two in step — it establishes and verifies the
connection, auto-imports what Whisparr grabs (with a polling backstop behind the webhook), and lets you
push to Whisparr:

- Distinct, actionable connection results (bad key vs unreachable vs a proxy page vs an unsupported
  version).
- The detected version and instance name on success.
- A ready-to-use webhook URL with an embedded secret, with best-effort auto-register.
- A background reconcile backstop that imports anything the webhook missed, in place — it never moves
  or deletes a file, and changes nothing in Whisparr.
- Automatic import of what Whisparr acquires, on both **v3 (Eros)** and **v2**. On v2, scenes match
  by their ThePornDB id rather than by StashDB id — see
  [Reconciliation](./reconciliation.md#matching-on-whisparr-v2).
- One-click **monitoring** of a studio or performer from its Cove page — studios on both v3 and v2
  (via ThePornDB), performers on v3 — with a quiet status line showing how many of its scenes Whisparr
  already has, out of the entity's full catalog — see [Monitoring](./monitoring.md).
- Opt-in, off-by-default **Whisparr status** for your library — a badge on each card (scene state:
  monitored / unmonitored / not added / excluded, with a second marker when Whisparr holds the file;
  studio/performer: a "Monitored · present/catalog" count) plus a library-level count row, both behind
  one toolbar pill, and the same state in the scene detail Whisparr tab — see
  [Whisparr status](./status.md).
- A per-entity **Missing** tab on studio, performer, and tag pages — a card grid of the scenes they
  offer that Cove doesn't own yet (catalogue minus owned minus excluded), with a live count badge,
  title search, sort, context-aware filters, and per-card and bulk **Monitor / Unmonitor / Search**.
  The studio, performer and tag tabs all work on Whisparr v3 and v2; the per-scene Monitor, Unmonitor
  and Search controls are v3 only. Cards are rich (cover, performer avatars, tags, description) when
  read from Cove's own metadata source — see the
  [Missing tab reference](./discovery.md) and [Find the scenes you're missing](./find-missing-scenes.md).
- A read-only **Wanted, queue & history** page in its own home — what Whisparr is still looking for,
  downloading now, and has already acquired. Read live and uniform across v3 and v2 — see
  [Wanted, queue & history](./activity.md).
