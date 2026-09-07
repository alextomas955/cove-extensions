# Recorded Whisparr wire answers

Answers read off a live, seeded Whisparr instance rather than from a document. Each is committed beside
the conditions it was taken under — the instance version string and the seeded set size — because a
reading without its conditions cannot be compared to a later one.

**Instance:** `ghcr.io/hotio/whisparr:v3`, version **3.3.4.794**, branch `eros`.
**Seeded set size:** **9** movie rows, from the wholly synthetic corpus in `../wire-seed/`: three scenes
and two movies under one studio, the three rows the studio-attribution measurement needs, and one row
carrying a cover image. The seed also leaves one studio and one performer KNOWN to Whisparr holding
**zero** movies, which is a case an empty instance cannot present.

The cover row is the newest addition and it earns its place: every other record serves `Images: []`, so
on a corpus without it a cover-narrowing query parameter has nothing to act on and answers byte-for-byte
what the plain read answers — which is indistinguishable from the parameter being ignored. Readings taken
at a seeded set of **8** are the same measurements one row earlier; the git history of each file is where
the two sit side by side.

The artifacts here are written by `../../node-tests/wire-filter-control.test.mjs`,
`../../node-tests/wire-measurements.test.mjs` and `../../node-tests/bound08-ledger.test.mjs` under
`WIRE_RECORD=1`, and **verified** against the live instance on every ordinary run — a change in
Whisparr's behaviour fails the build rather than silently rewriting the file.

## Take a reading on an IDLE machine

**This suite is load-sensitive, and a busy machine produces false reds.** Run concurrently with a
`dotnet test` pass, four artifacts failed on `studioKnownWithNoMovies: committed 200 — live 404`:
Studio Borealis was not known to the instance. On an idle machine the same suite is green, and a
40-second poll shows the studio stable at `200` throughout. So a red taken under load is not evidence
about Whisparr, and — just as important — a **green** taken under load is not the same claim as a
green taken idle. State the condition when you record either.

The seeding step is where it bites: the known-but-empty pair exists only because a row is added and
then deleted, and under load the instance has not finished registering the studio by the time the
delete lands.

**Deferred, with its condition.** The remedy is for the seeder to assert Borealis is known
immediately after the delete and retry the registration if it is not, which would make the fixture
self-checking instead of timing-dependent. It is deferred rather than taken because no measurement
here depends on it and the failure is loud rather than silent. **Take it when** a run on an
otherwise-idle machine reproduces the `studioKnownWithNoMovies` red even once — at that point the
condition is no longer "concurrent load" and the fixture is genuinely racy.

## The four-part filter control, observed red

`red-control-observation.json` is the control applied to the un-narrowed `GET /api/v3/movie` — the read
four push handlers issue today — with the filter dropped and nothing else changed.

| Part | Un-narrowed read | Narrow `?stashId=` read |
| --- | --- | --- |
| 1 — the seeded set is the asserted size and holds ≥ 3 rows | **PASS** | PASS |
| 2 — a present id returns exactly the row asked for, by identity | **FAIL** — 9 rows | PASS |
| 3 — a well-formed but absent id returns zero rows | **FAIL** — 9 rows | PASS |
| 4 — an unrecognised parameter returns the whole set | **PASS** | PASS |

Verbatim, from the committed observation at 3.3.4.794 with a seeded set of 9:

> part 2: `asked for 10000000-0000-4000-8000-000000000001, wanted identities
> ["10000000-0000-4000-8000-000000000001"], got 9 row(s) with identities
> ["10000000-0000-4000-8000-000000000001", "10000000-0000-4000-8000-000000000002",
> "10000000-0000-4000-8000-000000000003", "999001", "999002",
> "10000000-0000-4000-8000-000000000011", "10000000-0000-4000-8000-000000000021",
> "10000000-0000-4000-8000-000000000031"]`
>
> part 3: `asked for 10000000-0000-4000-8000-0000000000ff, got 9 row(s)`

Parts 1 and 4 passing on **both** legs is the point rather than an aside: they are the parts an ignored
filter cannot fail, so a control asserting only that "something failed" would pass for the wrong reason.
Naming which parts fail is what makes this a control.

Both legs run on every invocation of the spec, so the red observation is re-observable rather than a
line someone once saw scroll past.

## The movie row: what the shipped binary actually emits

`movie-row.json` holds one `GET /api/v3/movie` row captured verbatim, beside the metadata record the
stub served for it. Volatile values (`added`, `sizeOnDisk`, `traceId`, `lastSearchTime`, `freeSpace`) are
replaced with `<normalized>`; **no key is dropped**, because key presence is the measurement.

At 3.3.4.794, seeded set 9, from a metadata record carrying a studio and one performer credit:

| Field | Verdict |
| --- | --- |
| `studioForeignId` | **present, with a value** — `a0000000-0000-4000-8000-000000000001` |
| `studioTitle` | present, with a value — `Studio Aurora` |
| `performerForeignIds` | present, with a value — `["c0000000-0000-4000-8000-000000000001"]` |
| `performerNames` | present, with a value — `["Ava Bennett"]` |

The row carries all four. When this was first read, the extension recorded the opposite — that the live v3
movie row carries no studio foreign id — and compared `studioTitle` as a case-insensitive string for that
reason. The captured row agreed with the binary's own generated spec, which declares the field, and the
predicate now reads the identity (see the delta measurement below).

The pairing is what makes the answer usable: the served record's `Studio.ForeignIds.StashId` is exactly
the value the row reports, so a reader can tell that the field is populated **from** the studio identity
in the metadata — not that it happened to be non-null for an unrelated reason. A row seeded from metadata
with no studio would be silent on the question.

`performerForeignIds` is **deduplicated** and `performerNames` is **not**. A performer credited twice on
one record yields one id and two names (`["Mia Nakamura", "Mia Nakamura"]`). A caller counting performers
off `performerNames` therefore over-counts.

## What the instance's own API description declares

`openapi-capability.json` is the generated OpenAPI document the instance serves, reduced to the evidence a
narrow-catalogue capability is decided from. The document is fetched with **no `X-Api-Key` header**, and that
it answers anyway is part of the recorded answer rather than an assumption the extension carries.

At 3.3.4.794, seeded set 9:

| Reading | Answer |
| --- | --- |
| `GET /docs/v3/openapi.json` without a key | `200`, `application/json;charset=utf-8` |
| declared paths | **190** |
| the studio catalogue route, as the document spells it | `/api/v3/movie/listbystudioforeignid` |
| the performer catalogue route, as the document spells it | `/api/v3/movie/listbyperformerforeignid` |

The two key spellings are the artifact's reason for existing: the extension's capability decision compares
against these exact strings, and the unit test that pins the positive case reads them **from this file** rather
than from a second copy written by hand. A build that renames or drops a route fails the decision's own test,
not only this measurement.

The path count is recorded as a condition, not as a claim about any other build — it is set by the Whisparr
binary's API surface, which is why binding the whole document is bounded input here and would not be anywhere a
Cove library size reaches.

## Unknown entity versus known entity with zero movies

`sibling-endpoints.json` holds all four responses verbatim, plus the discriminator probes.

| Read | Unknown entity | Known entity, zero movies |
| --- | --- | --- |
| `GET /movie/listbystudioforeignid` | `200` `[]` | `200` `[]` |
| `GET /movie/listbyperformerforeignid` | `200` `[]` | `200` `[]` |

**Verdict: indistinguishable.** Status, content type and body are identical across the pair for both
siblings. This is a legitimate recorded answer, not a gap — but it means a caller reading only the
sibling cannot tell "Whisparr holds nothing for this entity" from "Whisparr has never heard of it", and
acting on the second as though it were the first is what would let an add-all-missing pass register an
entity's entire catalogue.

**The named discriminator**, recorded in the same run:

| Read | Unknown | Known, zero movies | Known, with movies |
| --- | --- | --- | --- |
| `GET /api/v3/studio/{studioForeignId}` | **404** | **200** | **200** |
| `GET /api/v3/performer/{performerForeignId}` | **404** | **200** | **200** |

Both answer `application/json` in every case; the 404 body is an RFC 9110 problem document carrying a
`traceId`. These are two distinct routes from `GET /api/v3/studio/{id}` and `GET /api/v3/performer/{id}`,
which take the integer row id — a foreign id is not an integer, so it routes to the by-foreign-id
overload. Both routes are declared in the instance's own OpenAPI document at
`/docs/v3/openapi.json`.

A caution worth carrying: probing `/studio/{foreignId}` for an entity that was never successfully
registered returns 404 and reads exactly like "the discriminator does not work". The known-with-zero case
here is real — Studio Borealis and Ivy Calloway are registered by adding a movie under them and deleting
it again, and `GET /api/v3/studio` lists Borealis with `sceneCount: 0`.

The sibling answers an **array of integer movie ids**, not movie resources
(`siblingWithMovies.body` is `[1, 2, 3, 4, 5]`), so a per-entity catalogue read needs a second read to
obtain rows.

## What `?stashId=` actually selects on

`stashid-predicate.json`, same instance and seed:

| Asked | Answer |
| --- | --- |
| the movie row's **StashDB id** (`20000000-…0001`, its `ForeignId` is `999001`) | `[]` — **not found** |
| that same row's **foreign id** (`999001`) | 1 row |
| a StashDB id carried by **two** rows (`10000000-…0001`) | exactly 1 row — the one whose `ForeignId` matches |
| an **empty** value (`?stashId=`) | **all 8 rows — the whole set** |
| an unrecognised parameter (`?bogusParam=`) | all 8 rows — the whole set |

Despite its name the parameter matches on `ForeignId`, so it finds a row only when that row's foreign id
happens to be its StashDB id — true for a scene added by StashDB id, false for a TMDB-sourced movie row
that merely carries one. A caller choosing this predicate is choosing the foreign-id lookup, and should
choose it deliberately rather than on the parameter's name.

The empty-value row is the one with teeth: an empty filter is not a no-op filter, it is a whole-library
read, and it answers `200` like any other.

## The two studio-attribution predicates, measured against each other

`studio-attribution-delta.json`. Both predicates are computed over **one** whole-set read, client-side,
exactly as the extension computes them — the title comparison requires a non-empty studio title and is
case-insensitive; the identity comparison is case-insensitive on the studio's own foreign id. Two reads
could have differed for a reason that was not the predicate.

Per named seeded studio, 3.3.4.794, seeded set 9. The title column is the count **before** the switch and
the identity column the count **after** it; the artifact's `shippedPredicate` names which one the extension
implements at the moment it was recorded, and the file's own history holds both readings.

| Studio | Studio's own title / id | Before (title) | After (identity) | Gained | Lost |
| --- | --- | --- | --- | --- | --- |
| Aurora | `Studio Aurora` / `a0…0001` | 5 | 5 | — | — |
| Cirrus | `Studio Cirrus` / `a0…0003` | 1 | 1 | — | — |
| Echo | `""` (no title) / `a0…0004` | 0 | **1** | `10000000-…0031` | — |
| Borealis | `Studio Borealis` / `a0…0002` | 0 | 0 | — | — |

Explained per entity, not averaged:

- **Aurora, 5 → 5.** Not "no change": all five of its rows carry both the studio title and the studio
  identity, so each satisfies **both** predicates and neither direction moves.
- **Cirrus, 1 → 1.** Its one row likewise satisfies both. The row was seeded to disagree — the scene served
  under it carried the title `Cirrus Productions` — and the disagreement did not survive to the wire,
  because Whisparr writes the **site record's** title onto the row.
- **Echo, 0 → 1.** The gain, and the only movement in the table. Row `10000000-…0031` carries the studio
  identity `a0…0004` and an empty studio title, so the title comparison could never reach it while the
  identity comparison resolves it directly.
- **Borealis, 0 → 0.** Registered and holding no movies, so both predicates answer zero over an empty set.

**The lossy direction is empty**, and the corpus was extended specifically so that could be a finding
rather than an absence of looking. Three rows were seeded, each pairing a served metadata studio against
what the instance actually emitted:

| Seeded row | Served studio | Row's `studioTitle` / `studioForeignId` | What it establishes |
| --- | --- | --- | --- |
| `10000000-…0011` | title `Studio Aurora`, **no** id | `null` / `null` | Whisparr does not populate a studio title without an identity — it attaches **no studio at all** |
| `10000000-…0021` | title `Cirrus Productions`, id `a0…0003` | `Studio Cirrus` / `a0…0003` | The row's title comes from the **site record** the studio entity resolves from, not from the title embedded in the scene, so the two cannot diverge by that route |
| `10000000-…0031` | title `""`, id `a0…0004` | `""` / `a0…0004` | The **gaining** class: a studio with no title is invisible to the title predicate and resolvable by the identity one |

So on this build the shape that would make the switch lossy — a row carrying a studio title but no studio
identity — is one Whisparr declines to emit. That is a statement about **this** build's behaviour with
**this** corpus, not a guarantee about rows an older Whisparr wrote into an existing library.

**Population, as a fraction rather than a percentage:**

| Over the seeded set | Fraction |
| --- | --- |
| rows carrying a non-empty studio title | **6 / 8** |
| rows carrying a non-empty studio foreign id | **7 / 8** |
| rows carrying a title but **no** foreign id | **0 / 8** |

The corpus is **wholly synthetic**. It therefore establishes the delta **classes** and their **direction**
— which way each disagreement runs, and which shapes can produce one at all — and it establishes **nothing**
about what fraction of a real library's rows carry a studio identity. Every row here was added through the
replay stub in the same session, so the fraction above describes the seeder, not a library that accumulated
over years across several Whisparr versions. What would establish the real rate is the same measurement leg
run against a real instance and its recorded fraction reported beside these conditions — a reading someone
can take, not a promise this record makes.

## The narrowed per-entity read, under the same four-part control

`entity-catalogue-narrow.json`. The control is applied twice over one seeded instance: once to the narrowed
per-entity read (sibling ids → client-side dedup → chunked by-id hydration) and once to the un-narrowed
whole-movie-set read it replaces. Part 2 compares an identity **set** here rather than a single row, because
an entity id selects an entity's whole catalogue; it is still identity, never a count.

| Part | Un-narrowed read | Narrowed read |
| --- | --- | --- |
| 1 — the seeded set is the asserted size and holds ≥ 3 rows | **PASS** | PASS |
| 2 — a present id returns exactly the rows that id selects, by identity | **FAIL** — 9 rows for a 5-row studio | **PASS** — exactly Aurora's 5 |
| 3 — a well-formed but absent id returns zero rows | **FAIL** — 9 rows | **PASS** — 0 rows |
| 4 — an unrecognised parameter returns the whole set | **PASS** | **FAIL** — 1 row |

**Part 4's failure on the narrow leg is an answer, not a defect.** The sibling route does **not** behave like
the movie index for a parameter it cannot bind: it answers `200` with **one** row, not the whole set of 9. So
the failure mode of an unset id differs by route — on the movie index it is a whole-library read, here it is a
narrow wrong answer. Both are refused at the client edge, for different reasons.

What that one row *is* is recorded verbatim in `returned` rather than characterised. A first reading of it as
"the rows carrying no studio identity" was **wrong** — the row it returns carries a `null` foreignId — and the
assertion is deliberately narrowed to the single thing the reading establishes: this route does not read the
whole set. Anything more would be a claim the measurement does not support.

### What one entity costs

| Entity | k (deduped ids) | Requests | Formula | State |
| --- | --- | --- | --- | --- |
| studio Aurora (holds rows) | 5 | **2** — sibling + one `POST /movie/bulk` | `1 + ceil(k/1000)` | `known` |
| studio Borealis (known, holds nothing) | 0 | **2** — sibling + `GET /studio/{foreignId}` → `200` | `1 + 1 existence read` | `known` |
| studio unknown to Whisparr | 0 | **2** — sibling + `GET /studio/{foreignId}` → `404` | `1 + 1 existence read` | `entityUnknown` |
| performer Ivy (known, holds nothing) | 0 | **2** — sibling + `GET /performer/{foreignId}` → `200` | `1 + 1 existence read` | `known` |
| performer unknown to Whisparr | 0 | **2** — sibling + `GET /performer/{foreignId}` → `404` | `1 + 1 existence read` | `entityUnknown` |

The existence read is issued **only** when the id list came back empty: a non-empty answer already proves
Whisparr knows the entity, so the common case never pays for it.

**Largest hydration response:** 5 rows, **6 936 bytes**, ≈ 1 387 bytes/row — recorded beside its row count so
the two can be read together. The chunk grain (1 000 ids per call) bounds the PER-CALL payload at roughly
1.7 MB at that row size; it does not bound an entity's catalogue, which is bounded by the entity.

**The twice-credited performer:** the sibling answered **one** id, not two. The undeduped upstream `Credit`
join did not produce a duplicate on this build, recorded as observed — the client-side dedup is defence at the
edge, and a build that starts emitting the duplicate fails this artifact rather than being silently absorbed.

## The missing set, computed both ways, per entity

`missing-set-delta.json`. For each seeded entity the missing set is computed twice over the SAME instance: the
whole-set diff shipped today (an owned scene is missing when its id indexes no row in the entire movie set)
and the per-entity narrow diff that replaces it (missing when its id indexes no row in **that entity's**
catalogue). The Cove-owned id set per entity is stated in the spec, not read from a Cove instance.

| Entity | Missing (whole-set) | Missing (narrow) | Gained | Lost |
| --- | --- | --- | --- | --- |
| studio Aurora | `…00ff` | `…0011`, `…00ff` | **`…0011`** | — |
| studio Borealis | `…00b1` | `…00b1` | — | — |
| studio Echo | — | `…0001` | **`…0001`** | — |
| performer Ava | — | `…0003` | **`…0003`** | — |
| performer Ivy | — | `…0001` | **`…0001`** | — |

**The gaining direction is not empty, and it is the one with teeth.** A row Whisparr holds but attributes to a
different entity is *not* missing under the whole-set diff and *is* missing under the narrow one, so the narrow
diff re-registers it. Whisparr answers already-added, this extension counts that as a **success**, and the
reader is never told. Four of the five entities gain a row, across three distinct shapes:

- `…0011` — a row Whisparr attributes to **no studio at all** (its metadata carried no studio identity).
- `…0001` under Echo — a row Whisparr attributes to a **different studio** than Cove does.
- `…0003` / `…0001` under a performer — a **credit Whisparr does not carry** on the row that Cove does.

The **lost** direction is empty for every entity, and that is structural rather than lucky: an entity's
catalogue is a subset of the whole movie set, so the narrow diff can never call a row present that the
whole-set diff called missing.

This establishes the delta **classes** and their **direction** on a wholly synthetic corpus. It establishes
nothing about how often a real library carries a row of any of those shapes.

## `whisparrCacheMovieAPI`, three labelled legs per sibling

The flag is invisible to a client, and it switches `listByPerformerForeignId` — but **not**
`listByStudioForeignId` — between two implementations, so the two siblings behave differently on the same
instance and a single unlabelled number would describe neither. Each leg's flag value below is **read
back off the instance** after the write, never the value that was sent. The fresh-instance default is
`false`, and `PUT /api/v3/config/host/{id}` answers `202` with the value reading back changed.

Instance 3.3.4.794, seeded set **8** rows (`listByStudioForeignId` for Studio Aurora matches 5,
`listByPerformerForeignId` matches 3), recorded 2026-08-03:

| Leg | Flag read back | `listByPerformerForeignId` | `listByStudioForeignId` |
| --- | --- | --- | --- |
| **off** (fresh-instance default) | `false` | 3 ms, 3 rows | 3 ms, 5 rows |
| **on-warm** (flag set, a prior movie read done) | `true` | 10 ms, 3 rows | 3 ms, 5 rows |
| **on-cold-after-restart** (container restarted, first call) | `true` | 38 ms, 3 rows | 18 ms, 5 rows |

**These times are recorded, not asserted.** A millisecond threshold inside a containerized suite would
gate the test runner rather than Whisparr. What the spec asserts is each leg's conditions — the flag
value read back, that the restart was of the same container, and the seeded set size.

The asymmetry is the finding, and at this seed size it is visible but small: the performer path is the
slower of the two on every leg and degrades most on the cold leg, while the studio path has no
cache branch to degrade. **A studio-path figure does not describe the performer path**, and neither
figure extrapolates: nine rows is far below the size at which a whole-set materialisation behind a
global lock would be the dominant cost.

**Cold-cache lock warnings: none.** The instance emitted zero `[Warn]`/`[Error]` lines mentioning a lock,
a timeout or a cache after the cold leg. Recorded as an answer rather than omitted — an absence at nine
rows says the lock was not contended at this size, and says nothing at all about a real library.

## Today's whole-movie-set read counts, per path

Counted off the real client by `WhisparrSync.Tests/TestSupport/WhisparrRequestCounter.cs`, which
classifies the requests `FakeHttpMessageHandler` already captures. A request is a whole-set read only
when it is a GET of `/api/v3/movie` carrying no value for `tmdbId`, `tpdbId` or `stashId` — so a narrow
read can never be tallied as a whole-set one. An **empty** filter value counts as whole-set, matching
what the instance does with one.

Unlike everything above, these are not readings off a container: they are assertions in
`WhisparrSync.Tests`, so they fail a build when they change rather than needing to be re-taken.

| Path | Formula | Today |
| --- | --- | --- |
| Single-scene push | **zero** whole-set reads, one per-scene read per handler | **0 whole-set, 4 narrow** — `SceneSearchAsync`, `SceneGrabReleaseAsync`, `SceneReleasesListAsync`, `SceneSearchUpgradesAsync`, one narrow read each |
| Library sync | one whole-set read **per reflect-owned entity unit** | **studios-with-an-id + performers-with-an-id** |

The push figure was **4 whole-set reads** when first taken, one per handler, each pulling every movie
Whisparr tracks to find the one row its scene resolves to. The narrow count is pinned beside the zero
because a handler that stopped reading Whisparr at all would satisfy the zero on its own.

Both are v3 figures and neither describes v2, whose owned-import walk is series-shaped.

The library-sync figure is asserted as a formula rather than as a number, across five differently-shaped
libraries — including one whose entities all lack an id and therefore plan no unit and cost nothing. A
single shape would only ever have established a constant.

Because a narrowing is meant to drive the first figure to zero, the classifier's own ability to tell a
narrow read from a whole-set one is exercised directly. An instrument that called every movie read
whole-set would agree with both numbers above **and keep agreeing after a narrowing landed**, which is
the shape of gate that reports on something other than what it is believed to report on.

## `/movie/list` on both verbs, and whether `excludeLocalCovers` is honoured

`movie-list-probe.json` answers the two reads the library-wide summary was going to be built on. Both
were open questions with two incompatible readings on record, and both are settled here by a live call
rather than by a document — though the document is read too, and agrees.

### The same path serves two entirely different answers, one per verb

| Call | Answer | Label | Bytes / row |
| --- | --- | --- | --- |
| `GET /api/v3/movie/list` | 9 bare integer ids | `bareIds` | **5.22** |
| `POST /api/v3/movie/list` with `[]` | an empty array | — | — |
| `POST /api/v3/movie/list` with two foreign ids | 2 rows of **36 members** each | `fullRows` | **1461.5** |

Both readings on record were right, about different verbs, which is why neither could be confirmed by
argument. The instance's own document declares **`get` and `post`** for that one path, and the four-part
control separates them exactly:

| Part | `GET` verb | `POST` verb |
| --- | --- | --- |
| 1 — the seeded set is the asserted size | PASS | PASS |
| 2 — a present id returns exactly the rows it selects | **FAIL** — 9 rows | PASS — 1 row, by identity |
| 3 — a well-formed absent id returns zero rows | **FAIL** — 9 rows | PASS — 0 rows |
| 4 — an unrecognised parameter returns the whole set | PASS | **FAIL** — 0 rows |

The `GET` verb binds no id at all, so it carries the un-narrowed signature (`[2, 3]`) — it is a whole-set
read wearing a narrow name. The `POST` verb is a by-ids batch, and its part-4 failure is an answer of the
same kind the sibling routes gave: it does not widen to the whole set for a parameter it cannot bind.

**Neither shape serves the toolbar summary**, and the reason is recorded in the artifact rather than left
to be re-derived. A bare id carries neither the monitored flag nor the file flag, so a summary built on
ids would hydrate every one of them — the same rows in more requests. A by-foreign-ids batch needs the id
list up front, and the summary has none: it counts Cove videos, so producing that list means one id per
Cove video. So `decision.summaryRead` is **`streamedMovieIndex`**, and `/movie/list` contributes the
bytes-per-row figures above and nothing else.

Getting the verb wrong is not a neutral mistake: a `POST` to a `GET`-only route answers 405 with no JSON
content type, which the shared send loop classifies `NotWhisparr` — the user would be told their Whisparr
is not a Whisparr.

### `excludeLocalCovers` is honoured, decided three ways

| Read | Rows | Body bytes | `images` entry members |
| --- | --- | --- | --- |
| `GET /api/v3/movie` | 9 | **12 480** | `coverType`, `url`, `remoteUrl` |
| `GET /api/v3/movie?excludeLocalCovers=true` | 9 | **12 431** | `coverType`, `remoteUrl` |

Same identity set, fewer bytes, and the member that disappeared is the locally-cached cover path
(`/MediaCover/movie/9/poster.jpg`) — so the label is `honoured`. The other two labels the rule can produce
are `ignored` (same identities, same bytes) and `rejected` (different identities, in which case the
parameter must not be sent at all).

Three things make that label mean something. The corpus must serve at least one cover, asserted, or the
byte figures would be a statement about the corpus rather than about the parameter. The plain read is
taken **twice**, once on either side of the narrowed one, and asserted equal — a member that merely
settles between two reads would otherwise be attributed to the parameter. And the identity sets are
compared before the bytes are, because a parameter that changed which rows come back would be a
correctness change wearing a payload reduction's clothes.

`remoteUrl` **survives** the narrowing. That matters because the discovery projection reads it; the
parameter is nevertheless applied only to the summary's own read, so no other caller's rows can be
altered by a change made for the summary's benefit.

### The two paths this extension may not read

Recorded as observation only, because "the document declares it" was the evidence that made them look
usable in the first place: the instance declares **`POST /api/v3/movie/paged`** and
**`GET /api/v3/movie/stats`**. Nothing here reads either, and neither appears in any production source.

### What the summary pays besides the movie read

The exclusion set is read once per summary alongside the movie set. On this corpus it is **0 rows, 2
bytes** — an empty array. It is recorded because the summary's cost is the pair, not the movie read
alone, and a later reading of the pair needs both halves taken on the same instance.

## What the toolbar summary costs, at two corpus sizes

`summary-read-cost.json` is taken on its **own** instance, seeded with a corpus generated per run rather
than from `../wire-seed/` — growing the shared corpus would move every reading above. Each generated row
has one studio, no credits and one cover, so the cover parameter has something to act on at every size.

| Read | 12 rows | 120 rows | Bytes / row |
| --- | --- | --- | --- |
| `GET /api/v3/movie` | 18 860 B | 189 398 B | **1 571.67** → **1 578.32** |
| `GET /api/v3/movie?excludeLocalCovers=true` | 18 269 B | 183 386 B | **1 522.42** → **1 528.22** |
| `GET /api/v3/exclusions` | 2 B (0 rows) | 2 B (0 rows) | — |

**What is asserted is the agreement, not the bytes.** Per-row figures agree to within **0.42%** across a
tenfold size change, which is what shows the number carries no fixed term and that the larger read was not
truncated. The absolute counts are machine- and build-specific and are recorded only. The row count of the
larger read is asserted separately against the corpus size, because a cap would satisfy the ratio on its own.

The cover parameter saves **49.25 B/row at 12 rows and 50.1 B/row at 120** — one locally-cached cover path
per row. On a corpus whose rows carry no cover it saves nothing, which is why the generated rows carry one.

**Seeding cost**, printed rather than recorded (it varies run to run, and pinning a container's scheduling
would make it a gate): 12 rows in **385 ms**, then 108 more in **1 821 ms**.

### The four-part control at the larger corpus, on both reads

| Part | The shipped summary read | The narrow per-entity read |
| --- | --- | --- |
| 1 — the set is the asserted size | PASS | PASS |
| 2 — a present id returns exactly its rows | **FAIL** — 120 rows | PASS |
| 3 — an absent id returns zero rows | **FAIL** — 120 rows | PASS |
| 4 — an unrecognised parameter returns the whole set | PASS | PASS |

The shipped summary read carries the **whole-set signature deliberately**, and recording it as green would
be recording a narrowing that is not one: `excludeLocalCovers` reduces bytes and returns every row. What the
control proves here is the other read — the per-entity narrowing is green at **120** rows, not only at the
eight it was first taken on.

### Retained memory — measured in process, not on the wire

Measured by `WhisparrSync.Tests/SceneStatus/SummaryReadCostTests`, over a lazily generated body, sampled
while the body is still arriving and the index is already populated. Transcribed here rather than pinned in
the artifact for the same reason the elapsed times are: a managed-heap figure is machine- and
runtime-specific.

One run, on this host; the figures move a few percent between runs, which is why nothing asserts them:

| Path | 10 000 elements | 100 000 elements | Bytes / entry |
| --- | --- | --- | --- |
| Streamed narrow fold | 7 108 576 B (at 9 000 indexed) | 58 807 800 B (at 90 000) | **789.8 → 653.4** |
| Materialised wide rows | 33 913 248 B | 322 783 040 B | **3 391.3 → 3 227.8** |

**A constant factor of ≈4.9×, not an order.** Both columns grow with the movie set, and the test asserts
that they do — tenfold the elements retains at least five times the bytes on **both** paths. That growth is
the residual this phase does not remove: the index is still one entry per Whisparr movie. The materialised
figure excludes the response buffer that path also pays, which is already released by the time it is
sampled, so it is a lower bound on that path's true peak.

**The guard was falsified.** Replacing the lazy body with one the harness materialises up front makes "the
body is still arriving" false, and the test fails with

> the body was not still arriving at the sampling instant: produced 6 379 632 of 6 379 632 bytes, with 9 000
> entries in the index

rather than reporting a smaller number.

Re-taken on this host with that class alone in the process: **657.8 → 645.7 B/entry** streamed and
**3 391.4 → 3 227.7 B/entry** materialised. The materialised column reproduces to within 0.1 B; the streamed
column's smaller sample reads 132 B/entry below the figure above, which is the noisier of the two and is why
the pair is recorded as a range.

#### The older generation, whose set is synthesized rather than read

**What these figures describe is the per-site index FOLD, not a shipped toolbar summary.** The toolbar summary
is v3-only: no row this generation synthesizes carries a scene-level id, so the index it can build is empty for
a library of any size, and the four-state partition over it reported `notAdded` for every scene. The handler now
narrows to `IWhisparrStatusIndexSource`, which this generation does not declare, and refuses before the
transport. The fold survives unreached by any shipped path here, and is measured anyway because the property it
holds — a per-site walk hands each site's scenes over as it reads them and keeps one site rather than the
library — is a property of the SHARED walk that the materialised read's three callers do reach.

Measured by `WhisparrSync.Tests/SceneStatus/V2SummaryReadCostTests` through the shared
`TestSupport/V2SiteWalkProbe` harness, over a corpus generated in process — no instance is involved, and none
could be: what is being measured is what this side holds. The axis is the **site count**, varied tenfold with
the per-site scene count held **fixed at 300**, so the library grows while the largest single site — the term
a per-site walk is supposed to keep holding — does not.

Sampled **from inside the fold**, at the first scene the fold is handed from the target site. What is live at
that instant, and therefore inside the figure, is six things: the site list, the target site's
`WhisparrEpisode[]`, its `WhisparrEpisodeFile[]`, the `pathByFileId` dictionary `SynthesizeEpisodes` builds
from them, the scene being folded, and the accumulator.

| Path | 20 sites (6 000 scenes) | 200 sites (60 000 scenes) | Bytes / scene |
| --- | --- | --- | --- |
| Streamed per-site fold | 756 848 – 934 056 B | 275 160 – 3 349 992 B | — it does not scale with the library; the figure is one site plus the site list |
| Materialising composition | 7 093 328 – 8 075 656 B | 53 399 784 – 56 549 560 B | **1 182.2–1 345.9 → 890.0–942.5** |

**These figures differ from the ones previously recorded here because the sampling INSTANT moved, not because
the read changed.** The earlier instant sat inside the transport, as the target site's file response was being
served and before its body was composed — so it excluded that site's `WhisparrEpisodeFile[]` and the
`pathByFileId` dictionary built from them, which is part of the very per-site term the figure is cited as
bounding. It also included the transport's own in-flight response buffers, which are already collected by the
time the fold runs. The streamed 20-site column consequently reads **lower** than before (≈0.76–0.93 MB
against 3.39 MB), not higher: the domain term the instant now captures in full is smaller than the transport
transients the old instant caught mid-flight.

**Ten times the sites, streamed, retains less than a tenth of them did while the set was held** — which is
the statement that survives ambient noise, and the one the change claims. Recorded, never asserted: what the
tests assert are those relations, with additive slack, on samples taken inside one method.

Conditions the figures depend on, since a reading taken without them is a different reading — and these are
**weaker conditions than an isolated run**:

- The run was **not isolated**. Six runs of that class alone in the test process, on a host carrying a
  1-minute load average of **4.39–5.07** with Visual Studio Code's C# Dev Kit resident throughout — a
  `Microsoft.CodeAnalysis.LanguageServer` performing design-time builds against these same sources, plus a
  `vstest.console` host. Anyone re-taking these readings on a quiet host should expect different numbers, and
  should treat a mismatch as a difference in conditions before treating it as a regression.
- The **200-site streamed figure is unstable**, and the range above is the honest report rather than a
  representative sample: across six runs it read 275 160 · 3 349 992 · 1 391 456 · 306 360 · 1 384 528 ·
  1 382 448 B — a twelvefold spread clustering at roughly 0.29, 1.38 and 3.35 MB. Every one of those six runs
  ALSO rejected its first 200-site streamed window, each time on a walk-window floor fall of 1.52–1.55 MB, and
  succeeded on the retake. The other three columns are comparatively stable: the materialising column spans
  5.9% at 200 sites and the streamed 20-site column 23%.
- `GC.GetTotalMemory` is process-wide, so the retained-heap classes do not run alongside each other, and a
  sample whose process floor **fell** past the tolerance across **either** of its two windows — the harness
  construction or the walk — is discarded and re-taken rather than reported. A neighbouring corpus reclaimed
  inside a window once read as **−15 MB** of retention, and on other runs as a plausible figure an order too
  small. A sample where the fold was never handed the target site at all is rejected outright, since every
  assertion over the streamed figure is an upper bound and a zero would satisfy all of them.

**Both guards were falsified.** Sampling after the walk returns fails with

> no site was open at the sampling instant: 20 of 20 sites served against site 18 as the target — the target
> site's two reads must both have been served and at least one site must remain unwalked

and a transport that pre-builds every site's body — holding the very term the measurement claims was
deleted — fails with

> the pre-walk baseline scales with the site count: 4303960 B at 20 sites, 37551912 B at 200 sites — the
> harness is holding the corpus it claims the read does not

### Why this is a constant factor and not O(1)

The bounded Cove-side read the research asked to be confirmed **does exist** —
`ICoveLibraryPort.LoadOwnedRemoteIdsAsync`, added for discovery. It still does not make the summary O(1), for
a sharper reason than difficulty: it answers **id-set membership**, and the summary counts **Cove videos**.
The two diverge whenever the id↔video relation is not one-to-one, which this extension's own guidance already
records as live. `SummaryReadCostTests.Counting_matched_ids_is_not_counting_matched_videos` pins it on a
four-video fixture where one video carries two matching ids and one id is carried by two videos:
**5 matched ids against 4 matched videos**. Making page-wise counts exact would need a cross-page
distinct-video set, which is itself O(matched videos) — so the join relocates the term rather than removing
it, while changing the number the toolbar shows. The summary's peak is therefore
**O(Whisparr movie set) with a per-entry constant of ≈650–790 B**, down from ≈3 230–3 390 B. A much lower
ceiling, not the absence of one.

## The read-path ledger

`bound-08-ledger.json` is the single place the read path's cost is stated per path and per generation. It
runs on its **own** instance for the same reason `summary-read-cost.json` does, seeded with the same
generated corpus at the same two sizes (12 and 120), and it records three columns per row — response bytes,
retained bytes, request count — each carrying the run, test class or artifact behind it. A column with
neither a measurement nor a citation carries `null` and a stated reason; it never carries a number.

| Read | 12 rows | 120 rows | Bytes / row |
| --- | --- | --- | --- |
| `GET /api/v3/movie?excludeLocalCovers=true` | 18 269 B | 183 386 B | **1 522.42** → **1 528.22** |
| `GET /api/v3/exclusions` | 2 B (0 rows) | 2 B (0 rows) | — |
| `GET /api/v3/movie/listbystudioforeignid` | 65 B | 734 B | **5.42** → **6.12** |
| `POST /api/v3/movie/bulk` | 18 860 B | 189 398 B | **1 571.67** → **1 578.32** |

**Seeding cost**, printed rather than recorded: 12 rows in **356 ms**, then 108 more in **1 817 ms**.

Two readings need their reason stated or they will be misread.

**The per-entity catalogue is narrow in its ROW SET, not in its payload.** Its hydration answers the same
full rows the whole-set read does — 1 578 B/row at 120, matching `GET /api/v3/movie` to the byte. What it
saves is every row the entity does not own, which is the whole point; expecting a smaller per-row figure
from it is expecting the wrong narrowing.

**The sibling read's per-row figure is recorded and NOT asserted.** It answers bare integer ids, so its
per-row cost is the id's decimal width — 5.42 B/row over ids 1–12, 6.12 over 1–120. That disagreement across
the tenfold change is the id space growing, not a fixed term, so the agreement rule the other reads are held
to is the wrong instrument for it. Its row COUNT is asserted, which is what a truncated answer would fail.

The in-process columns are cited rather than re-measured here: retained bytes from `SummaryReadCostTests`
(above) and `SyncFanOutCostTests` (≈556 B per parked unit, 66 units at both 1e6 and 1e7 scenes), and the
request counts from `SummaryIndexEquivalenceTests` and `BoundedReadLedgerTests`.

### The residual the ledger names

`BoundedReadLedgerTests` measures the bulk mark-wanted path re-deriving its root folder and tag list for
every scene: **7 context reads at 3 scenes, 61 at 30 — a slope of exactly 2.00 per scene**, plus one
quality-profile read once. That is a fixed cost PER SCENE, which is O(library) in requests across a full
sync, and nothing in the read narrowing touched it. The sibling batch add resolves once for the whole batch
and its count does not move with the scene count at all, which is what makes the per-scene slope a
measurement of the path rather than of the harness — and is also the shape that would close it.
