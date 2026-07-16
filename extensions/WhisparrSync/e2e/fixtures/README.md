# Synthetic fixtures

The WhisparrSync end-to-end suite's **synthetic** media and metadata corpus. The suite seeds it into
a running Cove instance so every spec — and the documentation screenshot capture — runs against one
known dataset instead of inventing its own media.

Everything here is deliberately fake and publish-safe. There is **no real image and no real-person
content** anywhere — this repository is adult-content-adjacent and open source, so every thumbnail is
a tiny generated placeholder and every studio, performer, and scene name is invented
("Studio Aurora", "scene-alpha"). Keep it that way: the corpus is small and hand-maintained, so the
content-safety rule is a review rule (see [Maintaining the corpus](#maintaining-the-corpus)).

## Content-safety exception: the real-identity allowlist

The synthetic-only rule has **one deliberate, narrowed exception**: a small
committed **allowlist of real, SFW-sounding metadata-source ids/names/descriptions** in `allowlist/`
(StashDB for v3, ThePornDB for v2), plus the scrubbed SkyHook responses in `skyhook/` that resolve them.

> Synthetic media + metadata everywhere, **except** that small allowlist of real, SFW-sounding
> metadata-source **ids / names / descriptions** — **never images, never explicit text**.

It exists because Whisparr validates every add/monitor/search id against its own metadata source
(`api.whisparr.com`): a fully-synthetic id resolves to zero rows, so the outward call can only no-op to an
attribution-only result and there is nothing real to assert. Only the identity metadata is real; images
stay synthetic (seeded from `thumbnails/`) and every recorded response is scrubbed of images and
explicit text. See `allowlist/README.md` and `skyhook/README.md` for the exception rationale, the chosen
entries, and the captured wire contract. This is **not** a silent policy change — it is documented here
and in those two READMEs.

## Layout

```text
fixtures/
├── README.md               # this file
├── metadata/
│   ├── studios.json        # fake studio records
│   ├── performers.json     # fake performer records
│   └── scenes.json         # fake scene records, one per Whisparr state
├── thumbnails/             # one tiny (32x32) placeholder PNG per record
├── allowlist/              # real-identity allowlist (content-safety exception) — ids/names only, no images
│   ├── README.md
│   ├── v3-stashdb.json     # real StashDB studio id (v3 / eros)
│   └── v2-tpdb.json        # real ThePornDB site id (v2, e.g. Tushy tvdbId 3417)
└── skyhook/                # scrubbed, recorded SkyHook responses that resolve the allowlist offline
    ├── README.md           # captured wire contract + the <WhisparrMetadata> override mechanism
    ├── index.json          # lookup path+query -> recorded response file
    └── *.json              # the scrubbed recordings
```

Thumbnail paths inside the metadata are relative to this `fixtures/` directory (for example
`thumbnails/scene-alpha.png`).

## Metadata shapes

All records use camelCase fields and stable key ordering; ids, `stashId`s, and `path`s follow
obviously-synthetic patterns.

A **studio** and a **performer** record each carry:

| Field | Meaning |
| --- | --- |
| `id` | Synthetic id (`studio-*` / `performer-*`). |
| `name` | Fake display name. |
| `thumbnail` | Path to the record's placeholder PNG. |

A **scene** record carries:

| Field | Meaning |
| --- | --- |
| `id` | Synthetic id (`scene-alpha` … `scene-epsilon`). |
| `title` | Fake display title. |
| `whisparrState` | One of the five states below. |
| `stashId` | Synthetic StashDB id (`stash-synthetic-000N`). |
| `path` | Fake on-disk path under `/synthetic/`. |
| `studio` | The owning studio's `id`. |
| `performers` | An array of performer `id`s. |
| `thumbnail` | Path to the scene's placeholder PNG. |

### Whisparr states

Each scene carries exactly one `whisparrState`, and the five scenes cover every state the status UI
renders. Each state's thumbnail is tinted a fixed color so a screenshot can tell them apart.

| `whisparrState` | Scene | Thumbnail tint |
| --- | --- | --- |
| `downloaded` | scene-alpha | green |
| `monitored` | scene-beta | blue |
| `unmonitored` | scene-gamma | slate |
| `not-added` | scene-delta | amber |
| `excluded` | scene-epsilon | red |

## How the suite uses it

`lib/seed-fixtures.mjs` reads these records and seeds them into a running Cove instance through the
Cove API (registering each scene from one shared tiny fixture video, then attaching its Cove-owned
title / studio / performers). Because every scene has a fixed `whisparrState`, a spec can assert the
status the extension reports for each of the five states. The documentation screenshots are captured
from a Cove instance seeded the same way.

## Maintaining the corpus

The corpus is small and committed directly — there is no generator. To add or change a case:

1. Edit the relevant `metadata/*.json` record. Keep names **obviously invented** (never a plausible
   real performer/studio name) and keep `id` / `stashId` / `path` on the synthetic patterns above.
2. Add a matching **tiny** placeholder PNG under `thumbnails/` (≤ 48×48, a flat/checkerboard fill —
   never a real photo).

**Never add real media or real-person content, and never point a fixture at a real StashDB id.** The
whole point of the corpus is that it is safe to publish; a code reviewer should be able to confirm
that at a glance from the invented names and the tiny placeholder images.
