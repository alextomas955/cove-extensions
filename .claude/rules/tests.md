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

## Test the real implementation, not the double

A test double exists so a caller can be driven, never as a subject.

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
