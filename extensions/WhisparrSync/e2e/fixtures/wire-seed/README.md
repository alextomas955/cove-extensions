# Wholly synthetic metadata corpus

Metadata records the SkyHook replay stub serves so a Whisparr instance can be seeded with a known,
deliberately-shaped movie set. `index.json` maps the metadata route Whisparr issues to the record served
back, exactly as `../skyhook/index.json` does.

**No content-safety exception applies here.** Unlike `../skyhook/`, nothing in this directory is a
recording of a real service: every id, title and name is invented, so the narrowed real-identity
allowlist described in `../README.md` is not in play. Keep it that way — a record here must never carry a
real metadata-source id.

## Why a second corpus rather than more entries in `../skyhook/`

`../skyhook/` is a captured contract: its records are scrubbed replays whose value is that they resolve
the same real ids a user's instance would, and re-capturing them is a documented procedure. These records
are hand-authored to make specific relationships true (a studio with no scenes, a performer credited
twice on one row, a movie whose `StashId` differs from its `ForeignId`). Mixing the two would put
hand-authored rows behind a "captured live" claim.

## The shapes and why each exists

| Record | Shape it makes true |
| --- | --- |
| `site-aurora.json` | A studio several movies attribute to. |
| `site-borealis.json` | A studio that ends the seed known to Whisparr with **zero** movies. |
| `performer-ava.json` | A performer credited on movies. |
| `performer-ivy.json` | A performer that ends the seed known with **zero** movies. |
| `performer-mia.json` | Credited **twice on one movie**, so the credit join can be observed for duplicates. |
| `scene-one.json` · `scene-two.json` · `scene-three.json` | Scene rows whose `StashId` equals their `ForeignId` — the primary-predicate case. Scene three carries no credits. |
| `scene-borealis.json` | Added and then deleted, which is what leaves Borealis and Ivy known-but-empty. |
| `movie-differing-id.json` | An `ItemType: "Movie"` row whose `StashId` differs from its `ForeignId`. |
| `movie-duplicate-id.json` | A second row carrying the SAME `StashId` as `scene-one.json`. |
| `scene-aurora-no-identity.json` | A scene whose served studio carries a title but **no** identity — the shape that would make a title→identity attribution switch lossy. |
| `site-cirrus.json` · `scene-cirrus.json` | A studio whose own title differs from the title embedded in the scene served under it, so the two possible sources for a row's `studioTitle` can be told apart. |
| `site-echo.json` · `scene-echo.json` | A studio with **no title at all**: attributable by identity, invisible to a title comparison. |
| `site-covers.json` · `scene-cover.json` | The only row carrying a cover image. Every other record serves `Images: []`, so on a corpus without this row a cover-narrowing query parameter has nothing to act on and answers byte-for-byte what the plain read answers — which is indistinguishable from the parameter being ignored. It is credited to nobody and attributed to its own studio so no other measurement's counts move. |

## Record shape

Records mirror the PascalCase wire shape captured in `../skyhook/` — Whisparr's hosted metadata service
speaks it and the stub replays it verbatim. Two members are load-bearing and not obvious:

- `ItemType` selects the route Whisparr resolves the record on: `1` is a scene (`GET /scene/{stashId}`),
  `"Movie"` is a movie (`GET /movie/{tmdbId}`).
- Each `Credits` entry is a `CastResource`, and Whisparr dereferences its `Performer` member
  unconditionally while mapping the cast. A bare performer object in that slot faults the add with a
  `500 NullReferenceException` rather than being ignored, so the wrapper is required.
