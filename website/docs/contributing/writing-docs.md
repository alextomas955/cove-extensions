---
sidebar_position: 10
---

# Writing docs

The extension pages on this site are written for people who run Cove, not for developers. Aim for a
reader who has never opened the extension's settings and wants one thing done.

## Where the files live

- An extension's user docs live in `extensions/<Name>/docs/`. The site reads that folder directly, so
  there is no second copy to keep in step.
- Screenshots and animations go in `extensions/<Name>/docs/img/`.
- Contributor pages, like this one, live in `website/docs/contributing/`.
- The home page is `website/src/pages/index.tsx`.

## Page types

Each page does one job. Renamer's docs show the layout.

| Page            | What it is for                                                                                                 |
| --------------- | -------------------------------------------------------------------------------------------------------------- |
| Overview        | What the extension does, a screenshot, a before-and-after example, and where to go next.                       |
| Quick start     | Install to first result in numbered steps, one screenshot or animation per step that changes the screen.       |
| How-to guides   | One goal per page: a short intro, numbered steps, a screenshot, then **Good to know** (a few bullets at most). |
| Troubleshooting | Every badge, message and rare case, with what to do about it. The edge cases live here, not in the guides.     |
| Reference       | Every setting or token, in the order the UI shows them. Neutral and complete.                                  |
| Changelog       | User impact per release. See [Releasing](./releasing).                                                         |

A page for contributors, such as how an extension works inside, goes under a **For contributors**
category at the end of the extension's sidebar.

## Write steps people can follow

- Start the page with what the reader gets, in one or two sentences.
- One action per numbered step. Say where before what: "Under **Run & automation**, select **Dry
  run**."
- Write UI labels in bold, spelled exactly as the UI shows them.
- Include the last click, such as **Save changes**.
- Say "select", not "click", so the step reads the same with a keyboard or a touch screen.
- Keep implementation details out of user pages. The reader needs what happens, not how the job
  scheduler or the database does it.
- Show a template or command next to what it produces.
- Check every claim against the code or the running app before you write it.

## Take screenshots

Take screenshots from a clean Cove with invented data, never from your own library. Real file paths,
account names and titles end up on a public site.

1. Start a disposable Cove with the extension installed. `startHarness()` in
   `tests/e2e/lib/harness.mjs` does this in Docker, and `tests/e2e/lib/seed-media.mjs` adds media. Give
   the items made-up titles, studios and dates.
2. Drive it with Playwright at a 1440 × 1000 viewport with `deviceScaleFactor: 2` and
   `colorScheme: "dark"`, so every image is sharp and matches Cove's default look.
3. Wait until what you are showing has finished loading, such as every row of a table.
4. Crop a card with about 16 px of padding on every side, using its bounding box as the clip. Crop a
   dialog to its own border instead: padding around a modal shows fragments of the dimmed page
   behind it. Avoid full-window shots except on an overview page.
5. Save as JPEG at quality 88. A card is usually 30 to 150 KB.
6. Open every image, and every frame of an animation, and check nothing is cut off. Extract frames
   with `magick demo.webp -coalesce frame-%02d.png`.

For something that moves, such as a preview updating as you type, capture one frame per state and
join them into an animated WebP with ImageMagick:

```sh
magick -loop 0 -delay 150 f000.png f001.png -delay 300 f002.png \
  -resize 1400x -strip -quality 82 -define webp:method=6 demo.webp
```

`-delay` is in hundredths of a second and applies to the frames after it. Keep an animation under
about 300 KB and a few seconds long.

## Strip metadata before you commit

GitHub does not remove metadata from files in a repository. Strip it yourself as the last step,
after any cropping or resizing, because re-saving an image can add headers back. Then check:

```sh
exiftool -all= -overwrite_original extensions/<Name>/docs/img/*
exiftool -a -G1 -s extensions/<Name>/docs/img/* | grep -v -E '^\[(System|ExifTool|File|Composite)\]'
```

The second command should print only image structure, such as width, height and animation timing.
Then look at every image yourself for names, paths or anything else that is yours.

## Add an image to a page

```md
![The Where files go section with a folder template of $studio/$year.](./img/where-files-go.jpg)
```

- Write alt text that says what the image shows, in one sentence under about 150 characters.
  Someone reading only the alt text should still follow the page.
- Put the image right after the step it illustrates.
- The site adds the frame and shadow. Don't add borders or text inside the image.

## Markdown and MDX

- A `.md` page is plain CommonMark. Rename it to `.mdx` to use an admonition or a component.
- Write an admonition title in brackets: `:::tip[Run a dry run first]`.
- In `.mdx`, keep `{`, `}` and `<` inside backticks, and don't use `&` in alt text.
- Link to another page with a relative path and no extension, such as `../settings#where-files-go`.

## Preview the site

```sh
cd website
npm ci
npm start          # live preview while you edit
npm run build      # the check CI runs; fails on any broken link
```
