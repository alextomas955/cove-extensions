# Recorded job-progress captures

What the Job Drawer actually showed during a library sync, read from the live instance rather than
reasoned about. Each capture is a JSON array of every `GET /api/jobs/{jobId}` response taken at roughly
1 Hz for one run, each with the wall-clock time it was taken (`t`) and the milliseconds since the
`/sync-library` POST returned (`sinceStartMs`). That endpoint serves the exact `JobInfo` the drawer
renders, so a claim about the bar, the denominator or the ETA is a claim about these files.

Every read is scoped by the `jobId` the POST returned. Reading "the newest job" would let an unrelated
host job satisfy the assertion.

**Conditions.** Cove `cove-app:dev`, extension `com.alextomas955.whisparrsync` published from this
repository immediately before each run and verified by comparing the deployed assembly's hash to the
freshly built one. Whisparr `ghcr.io/hotio/whisparr:v3`, version **3.3.4.794**, branch `eros`. Cove
library: **6000** videos, each carrying a StashDB id, and **no** studios or performers carrying one — so
the whole fan-out is the scene bucket, with no entity units mixed in.

Neither capture carries a Whisparr base URL, API key or webhook secret; the drawer payload carries only
extension-authored description and subtask strings, and both files are scanned for a credential before
they are committed.

## `sync-progress-before.json`

One run against the per-scene fan-out. **6000 units**, 37 samples, 36 s.

The denominator is the units registered *so far*, not the run's total, and at one unit in flight those
advance together. One second in, the drawer read `unitsTotal: 124`, `unitsCompleted: 123`,
`progress: 0.9919` — 99.2% of a run that had done 2% of its work — and it stayed above 0.999 for the
remaining 35 seconds. `etaSeconds` reads **0.0** from t≈4 s to the end, because "remaining" is computed
from a completed count that is always within one of its own total. The end-of-run summary string is
emitted repeatedly mid-run for the same reason ("123 of 123 units succeeded" at t=1 s).

## `drawer-before.png`

The same behaviour as a human sees it, from a separate run of the identical build: the bar full, reading
**"100% · 7s elapsed"** and **"finishing…"**, seven seconds into a 35-second run with 1014 of 6000 scenes
done. The numbers in the JSON are the falsifiable evidence; this is the readable one. The drawer's
history panel in the same shot shows the captured run finishing at 6000 of 6000 after 35 s.

## `sync-progress-after.json`

One run against the sliced fan-out, over the same library on the same instance, read the same way.
**12 units**, 30 samples, 29 s.

`unitsTotal` is **12 in the first sample** and takes no other value in any of the 30. The bar reads
`0.0833` — one twelfth — at the first sample carrying a completed unit and climbs by a twelfth per unit
to 1.0. `subTask` carries the per-page scene position (`Scene 500 of 6000` … `Scene 5500 of 6000`), so
the run's real scene-level progress is visible while it runs even though a unit is a slice.

### The ETA

Judged against a stated threshold rather than an impression. SKEWED means any of: `etaSeconds` outside
half-to-double the true remaining at a post-warm-up sample; null where the estimator's own warm-up
(4 s) and minimum-sample (4 completions) thresholds are already met; or rising across two or more
consecutive samples at a steady completion rate.

**Verdict: SKEWED by that definition, on all three clauses** — and better on the same measure than what
it replaced, which is why it is recorded rather than reverted.

| Measure, post-warm-up samples | Before | After |
| --- | --- | --- |
| Samples in the half-to-double band | **0 of 33** | **18 of 20** |
| Median ratio of estimate to truth | **0.00** | **1.05** |
| Range of that ratio | 0.00 – 0.25 | 0.80 – 5.10 |

The three trips, each with what it actually is:

- **Out of band once**, at the last sample before completion: 2.3 s estimated against 0.5 s true
  remaining. A ratio is ill-conditioned as its denominator approaches zero; the absolute error is 1.8 s.
- **Null once**, at the terminal sample — where the job has already completed and `progress` is 1.0. A
  finished job has no time remaining to estimate.
- **Rising across two consecutive samples**, repeatedly. This one is real and structural. The estimator
  measures throughput as a count of recent completion timestamps divided by the span from the oldest to
  *now*, so between completions the span grows while the count does not and the estimate climbs — the
  host's own comment at that code says a stall lowers throughput and raises the ETA, so it is designed.
  Coarser units make it more visible: twelve completion events across 29 s leaves ~2.4 s gaps, and a
  1 Hz reader sees two or three rising samples inside each gap.

The estimator is the host's, so there is no extension-side lever for the saw-tooth; the one alternative
the extension does control — dropping pre-registration — is what produced the *before* column.

**The predicted mechanism was confirmed.** Pre-registration stamps every unit's start time at once, so a
unit's recorded duration is its cumulative wait and grows monotonically. That duration feeds only the
estimator's no-op classifier, which asks whether a duration is at least a quarter of the typical real
one; monotonically growing durations always clear it, so no unit is misclassified and the estimate is
unaffected by the inflation. What the prediction did not name is why the *before* ETA read `0.0`: the
estimator computes remaining as total minus completed, and the same denominator defect that pinned the
bar near 1.0 also made "remaining" nearly zero. The bar defect and the ETA defect were one defect.

### The pre-registration burst

Time from the `/sync-library` POST returning to the first sample carrying a non-null denominator, at
**12 units** over a **6000**-scene library, polled at 25 ms over three runs: **24 ms, 10 ms, 11 ms**.

That interval is a composite — job dequeue, the entity reads, the `COUNT(*)`, the pass over 6000 ids,
the planning and the twelve registrations. At twelve units the registrations are not separable from the
boundary pass at this fixture size, and the pass over the library is the larger of the two.

## `drawer-after.png`

The same run as a human sees it: the bar a quarter full reading **"25% · 8s elapsed"** and
**"~12s remaining"**, with **"Scene 1500 of 6000"** above it. The history entries below it show the
accepted trade in the same frame — a completed run reads **"12 of 12 units succeeded"**, which counts
slices, while the scene tally lives in the subtask line and the run's log entry.
