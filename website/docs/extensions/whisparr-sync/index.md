---
id: index
title: Whisparr Sync
---

Whisparr Sync connects Cove to a self-hosted [Whisparr](https://whisparr.com) v3 (Eros) or v2
instance. You enter your Whisparr URL and API key on a settings page, test the connection, pick a
root folder and quality profile from auto-populated lists, and generate a webhook URL Whisparr can
call. Your API key is stored server-side only — it is never shown back to you and never written to
logs.

## In this section

- [Connect guide](./guide.md) — set up Whisparr and Cove for your version (a **v3 (Eros)** section and
  a **v2** section), test the connection, pick a root folder and quality profile, and add the webhook.
- [Monitor a studio or performer](./monitoring.md) — turn Whisparr monitoring on from an entity's Cove
  page and read its status line.
- [Reconciliation](./reconciliation.md) — view matched / unmatched / needs-review and confirm or reject
  ambiguous matches.
- [Whisparr status](./status.md) — the per-scene states (downloaded / monitored / not added / excluded),
  how each is derived, and the three surfaces that show them.
- [Settings reference](./settings.md) — every setting on the page, with defaults and valid values.
- [Changelog](./changelog.md) — user-facing changes, newest first.

## What it does today

This release connects Cove to Whisparr and keeps the two in step — it establishes and verifies the
connection, reconciles your library against Whisparr, auto-imports what Whisparr grabs, and lets you
push to Whisparr:

- Distinct, actionable connection results (bad key vs unreachable vs a proxy page vs an unsupported
  version).
- The detected version and instance name on success.
- Auto-populated root-folder and quality-profile dropdowns.
- A ready-to-use webhook URL with an embedded secret, with best-effort auto-register.
- A read-only reconciliation view — matched / unmatched / needs-review, with inline confirm/reject
  for ambiguous id matches — that changes nothing in Cove or Whisparr.
- Automatic import of what Whisparr acquires, on both **v3 (Eros)** and **v2**. On v2, scenes match
  by their ThePornDB id rather than by StashDB id — see
  [Reconciliation](./reconciliation.md#matching-on-whisparr-v2).
- One-click **monitoring** of a studio or performer from its Cove page — studios on both v3 and v2
  (via ThePornDB), performers on v3 — with a quiet status line showing how many of its scenes Whisparr
  already has, out of the entity's full catalog — see [Monitoring](./monitoring.md).
- Opt-in, off-by-default **Whisparr status** for your library — a badge on each card (scene state:
  downloaded / monitored / not added / excluded; studio/performer: a "Monitored · present/catalog"
  count) plus a library-level count row, both behind one toolbar pill, and the same state in the scene
  detail Whisparr tab and the reconciliation table — see [Whisparr status](./status.md).
