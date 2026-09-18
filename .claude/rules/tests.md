---
paths:
  - "**/*.Tests/**/*.cs"
  - "**/*.test.{ts,tsx,mjs,js}"
  - "**/*.spec.{ts,tsx,mjs,js}"
  - "**/e2e/**/*.mjs"
---

# Tests

## An assertion that cannot fail is worse than no assertion

Before writing an assertion, name what would have to break for it to go red. If nothing can, delete
it.

Two shapes produce assertions that cannot fail:

- Asserting on a value the test itself supplied. Seeding a fake and asserting the fake returns what
  was seeded tests the fake.
- Asserting a counter stays empty when nothing increments it. `Assert.Empty(port.SaveCalls)` passed
  for a year on a member no production code called; the dry-run guarantee it claimed to protect was
  never checked.

A regression test must be observed failing before its fix, and passing after. Revert the production
change and run it. A test written after the fix, never seen red, has proven nothing.

Check the premise too. A test that supplies a zero free-space probe proves nothing when the move it
plans is same-volume, because the guard only measures cross-volume moves. A test that passes for the
wrong reason reads exactly like one that passes for the right one.

## Test the real implementation, not the double

A test double exists so a caller can be driven, never as a subject. There is no value in asserting
that a fake's dictionary returns what was put in it.

Where the real behavior needs a database, a volume or a filesystem, use the fixtures that supply
them and skip where the host cannot. A skipped test says so; a fake that agrees with itself does not.

## State the platform a test depends on

`File.Delete` refuses an open file on Windows and unlinks it on Linux. Directory permissions refuse
it on Linux and do not on Windows. CI runs Linux and Windows, so a test resting on either one's
semantics must branch on the platform or skip, and say which behavior it needs and why.

Assume nothing about path casing, path separators, mount semantics or file locking without naming
the platform that gives it.

## Orchestration needs behavior tests, not call-order tests

A class that only calls its dependencies does not need tests proving it called X before Y, unless
that order is a safety invariant, in which case say so in the test name.
