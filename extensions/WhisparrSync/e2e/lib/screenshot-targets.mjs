// Single source of truth for the docs feature-walkthrough screenshots: one canonical filename per
// Whisparr Sync surface. Two consumers read this list and must never drift:
//   - tests/screenshots.spec.mjs captures a real PNG per slot from the synthetic-fixture-seeded Cove.
//   - website/scripts/gen-screenshot-placeholders.mjs writes a labeled placeholder to any slot that
//     has no real capture yet, so a doc image link is never broken.
// This module is intentionally dependency-free (plain data, no Playwright import) so the website-side
// placeholder generator can import it without pulling the e2e harness in.

// Path of the docs image directory, relative to the repository root. Both consumers resolve their
// output against this so the capture and the placeholder write to the exact same files.
export const IMG_OUTPUT_SUBPATH = "website/static/img/whisparr-sync";

/**
 * The canonical slot set. `file` is the shipped image name; `surface` keys the capture spec's
 * navigation handler; `label` is the short human title the placeholder renders; `description` states
 * what the real screenshot shows.
 */
export const SCREENSHOT_TARGETS = [
  {
    file: "settings-connection.png",
    surface: "settings-connection",
    label: "Settings / Connection",
    description: "Settings page Connection section — Whisparr URL, API key, and Test connection.",
  },
  {
    file: "reconciliation.png",
    surface: "reconciliation",
    label: "Reconciliation table",
    description: "The read-only reconciliation table — matched, unmatched, and needs-review scenes.",
  },
  {
    file: "scene-panel.png",
    surface: "scene-panel",
    label: "Scene / Whisparr tab",
    description: "The scene-detail Whisparr tab — status, live controls, and releases.",
  },
  {
    file: "videos-batch.png",
    surface: "videos-batch",
    label: "Videos / Whisparr batch menu",
    description: "The videos-list multi-select Whisparr batch menu and its four ordered actions.",
  },
  {
    file: "monitor-studio.png",
    surface: "monitor-studio",
    label: "Studio / Monitor",
    description: "A studio page's Whisparr menu monitor toggle and the quiet grabbed-count status line.",
  },
  {
    file: "library-status.png",
    surface: "library-status",
    label: "Library / status toggle",
    description: "The videos toolbar Whisparr status toggle and its four-state legend.",
  },
];
