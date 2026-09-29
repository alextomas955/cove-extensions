---
paths:
  - "**/*.Tests/**/*.cs"
  - "**/*.test.{ts,tsx,mjs,js}"
  - "**/*.spec.{ts,tsx,mjs,js}"
  - "**/e2e/**/*.mjs"
---

# Tests

Test names carry the intent. Comments here follow `.claude/rules/comments.md`: a test needs one
only for a platform dependency or a non-obvious fixture fact.

## An assertion that cannot fail is worse than no assertion

Before writing an assertion, name what would have to break for it to go red. If nothing can, delete
it. Two shapes produce one:

- Asserting on a value the test itself supplied. Seeding a fake and asserting the fake returns what
  was seeded tests the fake.
- Asserting a counter stays empty when nothing increments it. `Assert.Empty(port.SaveCalls)` passed
  for a year on a member no production code called.

Observe a regression test failing before its fix and passing after. Revert the production change
and run it. A test written after the fix, never seen red, has proven nothing.

Check the premise. A test that supplies a zero free-space probe proves nothing when the move it
plans is same-volume, because the guard only measures cross-volume moves. A test that passes for
the wrong reason reads exactly like one that passes for the right one.

More shapes that cannot fail:

- A non-null check on a wrapper. A typed result union is an object even when it holds a 403.
  Assert the variant.
- A disjunction or a negation. "Skipped or failed" and "not gated" accept outcomes the code never
  produces. Assert the one outcome it does produce.
- An absence with nothing that could have produced a presence. Give the absence a positive
  control, or wait until the system has answered before asserting nothing arrived.
- A threshold that setup meets on its own. Count only what the unit under test creates.
- An input where two branches give the same output. If the name path and the id path render the
  same string, the test cannot tell which one ran.

## Expected values come from outside the code under test

Write the expected value as a literal copied from its source of truth: the server, the host, the
spec. An expected value computed with the module under test, or with a copy of its rule, agrees
with the code by construction.

## Drive the production entry point

A test that re-implements a production loop, query or lookup in test code tests the copy. When a
production constant makes the real path impractical, such as a page size too large to seed, make
the constant injectable and drive the real entry point.

## Keep test doubles thin

A test double exists so a caller can be driven, never as a subject.

- A fake holds the least its callers need. A fake that re-implements production logic drifts from
  it silently.
- A recording fake keeps what it records. A job service fake that drops the work delegate leaves
  everything inside the job untested.
- Code in `shared/` is tested once, in its own test project, against a small model of its own.
  An extension tests only its own use of it.

## Test the real implementation, not the double

Where the real behavior needs a database, a volume or a filesystem, use the fixtures that supply
them and skip where the host cannot. A skipped test says so; a fake that agrees with itself does
not.

## State the platform a test depends on

`File.Delete` refuses an open file on Windows and unlinks it on Linux. Directory permissions refuse
it on Linux and do not on Windows. CI runs both, so a test resting on either one's semantics must
branch or skip, and say which behavior it needs.

Assume nothing about path casing, separators, mount semantics or file locking without naming the
platform that gives it.

## Orchestration needs behavior tests, not call-order tests

A class that only calls its dependencies does not need a test proving it called X before Y, unless
that order is a safety invariant. Then say so in the test name.

## One test per path

Before adding a test, find the test that already reaches that state through the same code path.
Extend it instead of adding a second route to the same assertion. A handler test, an HTTP test
and an e2e spec asserting the same result on the same path cost three times and protect once.

A second test on the same path earns its place only with a property the first cannot see:
concurrency, scale, principal, cancellation, a real filesystem or host, or a cross-language pin.
Zero unique coverage is a reason to read a test, never on its own a reason to delete one.

Tests that share an arrange and differ only in input and expected value are one parameterized
test.

## The name states what the test asserts

If the name claims more than the assertions check, fix the test or the name. A name carries the
behavior, not the history of how it came to be.

## No tests of source shape

Do not reflect over declarations, enumerate enum member names, or read source files as text. They
break on a harmless refactor and pass on a broken behavior. The wire document already pins the
shape of every response.

## Concurrency tests must be able to provoke the race

A test that runs work in parallel and checks the result passes when the scheduler happens to
serialize it. Force the interleaving the race needs, or assert the invariant at every step, and
watch the test fail on code with the race in it.

## e2e tests leave the instance as they found it

- A spec that saves settings on a shared instance restores them, including on failure.
- Assert the environment's premise, such as two paths being on different filesystems. A fixture
  that silently stops providing it turns the spec into a test of the easy path.
- A job poll asserts the terminal status it expects. A failed job and a job that did nothing look
  the same to an assertion that checks only what did not change.
