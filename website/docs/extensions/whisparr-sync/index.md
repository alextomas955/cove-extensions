---
id: index
title: Whisparr Sync
---

Whisparr Sync connects Cove to a self-hosted [Whisparr](https://whisparr.com) v3 (Eros) instance.
You enter your Whisparr URL and API key on a settings page, test the connection, pick a root folder
and quality profile from auto-populated lists, and generate a webhook URL Whisparr can call. Your
API key is stored server-side only — it is never shown back to you and never written to logs.

## In this section

- [Connect guide](./guide) — enter your URL and key, test the connection, pick a root folder and
  quality profile, and add the webhook.
- [Reconciliation](./reconciliation) — view matched / unmatched / needs-review and confirm or reject
  low-confidence matches.
- [Settings reference](./settings) — every setting on the page, with defaults and valid values.
- [Changelog](./changelog) — user-facing changes, newest first.

## What it does today

This is the foundation release. It establishes and verifies the connection to Whisparr, captures the
settings later phases build on, and shows a read-only reconciliation of your library against Whisparr:

- Distinct, actionable connection results (bad key vs unreachable vs a proxy page vs an unsupported
  version).
- The detected version and instance name on success.
- Auto-populated root-folder and quality-profile dropdowns.
- A ready-to-use webhook URL with an embedded secret, with best-effort auto-register.
- A read-only reconciliation view — matched / unmatched / needs-review, with inline confirm/reject
  for low-confidence matches — that changes nothing in Cove or Whisparr.

Acquisition auto-import (acting on the webhook) arrives in a later phase.
