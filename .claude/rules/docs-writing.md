---
paths:
  - "website/docs/**"
  - "extensions/*/docs/**"
  - "**/README.md"
  - "**/CHANGELOG.md"
---

# Writing documentation

Match a page's length to what the reader needs. No preamble describing the page, no list of what
the page does not cover, and no closing summary.

## Keep the Diátaxis modes on separate pages

- How-to guide: one real goal, the user's perspective, a sequence of actions, no teaching.
- Reference: neutral and factual. Its structure mirrors the product: settings grouped by UI
  section, in UI order.
- Explanation: the why, and the design or safety model (`architecture.md`).
- Tutorial: rarely needed for one extension.

## Per kind

Settings reference, per setting: the label as shown in the UI, one neutral sentence, the default,
the valid values, and a short example when it helps. Uniform settings go in a table; one needing
nuance gets a subsection. Call out settings that exist but are not in the UI.

Template and token systems: open with one complete worked example (template in, exact filename
out), then a graduated series. Pair every token with its rendered output. Group tokens in thematic
tables. State syntax rules explicitly. Spell tokens as the product spells them (`$title`).

README versus site: the GitHub README is a short entry point plus contributor build and release
detail. The user story lives on the site.

Changelog: head the entry with the version it will ship as, never "Unreleased". List user impact
only; refactors, tests, renames and tooling do not appear. Lead with anything the user must do
before upgrading. Leave released entries as they shipped. Full rule:
`website/docs/contributing/releasing.md`.

## Style

Second person, active voice, present tense. Sentence-case headings. Task headings use the bare
infinitive ("Add a destination"); concept headings use noun phrases; no gerunds. Conditions before
instructions ("If X, do Y"). Example before prose. Common path first, advanced under its own
heading. Screenshots sparingly.

Docs name no planning or workflow tooling. No phase, plan, milestone, ticket or agent references.

Link to another site page with a relative doc link and no extension. Refer to a repo file in
backticks or with a full `github.com` URL. A markdown relative link resolves against site routes
and fails the build.

Verify every claim against the code before writing it. A documented setting the code ignores is a
defect: describe what the code does and report it.
