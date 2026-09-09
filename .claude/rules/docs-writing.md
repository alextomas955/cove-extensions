---
paths:
  - "website/docs/**"
  - "website/src/pages/**"
  - "extensions/*/docs/**"
  - "**/README.md"
  - "**/CHANGELOG.md"
---

# Writing documentation

The human version of this rule, with the screenshot commands, is
`website/docs/contributing/writing-docs.md`. Keep the two in step.

## Reader

Extension docs are for people who run Cove, not developers. Write for someone who has never opened
the settings page and wants one thing done. Implementation details (jobs, pages of rows, database
writes, serializers) stay out of user pages. They go in `contributors/` inside the extension's docs.

## Page set per extension

Follow Renamer's layout in `extensions/Renamer/docs/`:

- `index.md`: what it does, one screenshot, a before-and-after example, links onward.
- `quick-start.mdx`: install to first result in numbered steps.
- `how-to/*.md`: one goal per page. Short intro, numbered steps, one screenshot, then a **Good to
  know** list of at most five bullets.
- `troubleshooting.md`: every badge, message and edge case, with the fix. The rare cases live here so
  the guides stay short.
- Reference pages (`settings.md`, `templates.md`): complete and neutral, in UI order. Settings get a
  table row each: the label as shown, one sentence, the default, the valid values.
- `changelog.mdx`, then `contributors/` last.

Before adding a paragraph to a how-to, ask whether most readers need it. If not, put it in
troubleshooting or the reference page and link to it.

Update only pages whose claims or instructions change. Keep one canonical explanation and link to it
elsewhere; do not duplicate extension docs in a site wrapper that already imports them. Internal
refactors and test changes do not require user documentation or changelog entries.

## Style

- Second person, active voice, present tense. Sentence-case headings.
- Task headings use the bare infinitive ("Add a studio rule"). No gerunds.
- One action per numbered step, location before action: "Under **Run & automation**, select **Dry
  run**." Include the final click.
- UI labels in bold, spelled exactly as the UI renders them. Read them from the component source or
  the running app, never from memory.
- "Select", not "click".
- Show a template next to its output. Use a before-and-after tree for anything that moves files.
- Admonitions sparingly: at most one per section, never two in a row.
- No preamble about the page, no list of what it skips, no closing summary.
- Docs name no planning or workflow tooling. No phase, plan, milestone, ticket or agent references.

## Screenshots and animations

- Add a screenshot for every step that changes what the screen shows. Add an animated WebP when
  movement is the point, such as a preview updating as you type.
- Capture from a disposable Cove (the e2e harness in Docker) with invented data. Never from a real
  library: paths, account names and titles would be published.
- 2x device scale, dark scheme. Crop a card with about 16 px padding on every side, and a dialog to
  its own border, since padding around a modal shows fragments of the dimmed page. JPEG at quality 88. Animations as WebP under about 300 KB.
- Wait for the state you are showing to finish loading before capturing. A dialog captured while its
  rows load measures a fraction of its real size.
- Open every final image, and every frame of an animation, before committing. A crop that cuts off
  part of the UI is only visible there.
- Strip metadata with `exiftool -all= -overwrite_original` as the last step, because re-saving with
  another tool adds headers back. Then dump the tags again and confirm only image structure remains. GitHub does not strip it from repo files. Look at each image for personal
  data before committing.
- Never fake a UI state with DOM edits. If the state cannot be reached, leave the image out.
- Alt text says what the image shows, under about 150 characters. No `&` in alt text in `.mdx`.
- A UI change that alters a screenshotted screen recaptures that image in the same change.
- Capture scripts are not committed. Take the shots with Playwright directly.

## Mechanics

- `.md` is CommonMark. Use `.mdx` for an admonition or a component. Admonition titles use brackets:
  `:::tip[Title]`. In `.mdx`, keep `{`, `}` and `<` inside backticks.
- Link to another site page with a relative doc link and no extension. Refer to a repo file in
  backticks or with a full `github.com` URL.
- A renamed or removed page gets an entry in the `plugin-client-redirects` list in
  `website/docusaurus.config.ts`.
- `npm run build` in `website/` fails on a broken link. Run it before committing.

## Accuracy

Verify every claim against the code or the running app before writing it. A documented setting the
code ignores is a defect: describe what the code does and report it.

## README and changelog

- The GitHub README is a short entry point: what it is, how to install, a link to the site, then the
  contributor build and release detail. The user story lives on the site.
- Changelog: head the entry with the version it will ship as, never "Unreleased". List user impact
  only. Refactors, tests, renames, tooling and docs changes do not appear. Lead with anything the user
  must do before upgrading. Leave released entries as they shipped. Full rule:
  `website/docs/contributing/releasing.md`.
