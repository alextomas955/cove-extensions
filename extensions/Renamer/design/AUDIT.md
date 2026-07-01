# AUDIT.md — Renamer vs. Canonical Design System

**Audited:** 2026-07-01
**Scope:** Renamer's Settings Panel (only live UI surface) vs. COMPONENTS.md / EXTENSION_UI.md / DESIGN_SYSTEM.md (Phase 14, verified Phase 15)
**No refactor performed — findings only.**

## Note on Scope

This audit covers the 8 JSX-rendering file groupings under `I:\cove-dev\extensions\extensions\Renamer\src\Renamer.Ui\src\` that render UI chrome: `RenamePage.tsx`, `RenameSettingsPanel.tsx`, `primitives.tsx`, `dialog.tsx`, `PreviewCard.tsx`, `WarningBadge.tsx`, `TokenLegend.tsx`, and the `DryRunModal.tsx`/`UndoSection.tsx`/`entityPicker.tsx`/`studioMap.tsx` consumer group.

**Explicitly excluded:** `renameSelected.ts`, `preview.ts`, `options.ts`, `templateValidation.ts`, `presets.ts`, `dryRunLogic.ts`, `entityPickerLogic.ts`, `primitivesLogic.ts`, `studioMapLogic.ts` — these are pure `.ts` logic files with zero JSX and no design-system surface (no rendered markup, no Tailwind classes, no visual output to compare against a canonical doc). Mirroring `COMPONENTS.md`'s own "Note on Scope" exclusion pattern: this table covers reusable UI chrome only, and these 9 files contain no chrome at all.

## Surface-Level Contract Check

| Contract | Renamer's Behavior | Verdict |
|---|---|---|
| SectionCard heading-slot rule ("do not add an outer card or duplicate the heading") — `EXTENSION_UI.md:25` | `RenamePage.tsx:13-15` renders `<RenamePanelBody />` and nothing else — no outer `<div>`, no `<h1>`/`<h2>`/`<h3>` heading, no page gutter. The file's own header comment (`RenamePage.tsx:1-10`) explicitly reasons about this: "the host already supplies the tab header ... and a section card around this component, so adding our own page header/gutter here would triple the 'Rename' title." `RenameSettingsPanel.tsx:408-413` (`RenamePanelBody`'s own header comment) independently confirms: "The root stays a plain `<div className="space-y-6">`; the host SectionCard / page wrapper supplies outer chrome." | PASS |

## Per-Component Findings

### RenamePage.tsx — settings-tab entry point

No divergence findings for this file — its sole compliance point (the SectionCard heading-slot rule) is documented as a PASS in the Surface-Level Contract Check above, and cited again in Positive Findings below. No Per-Component Findings table is populated here since there is no divergence to tabulate.

### RenameSettingsPanel.tsx — main settings body

| Property | Renamer | Canonical | Divergence | Severity | Bring-it-in-line |
|---|---|---|---|---|---|
| `Panel` sub-card shell (component-level; used 6× at `RenameSettingsPanel.tsx:733,802,846,926,1169,1404`) | `Panel` (`RenameSettingsPanel.tsx:314-323`): `rounded-2xl border border-border bg-surface p-5` | `SectionCard`/`SettingsSection` (`SettingsPrimitives.tsx:104-121`; measured geometry table `EXTENSION_UI.md:9-19`): `p-5` / `rounded-2xl` / `bg-surface` / `border-border` | Byte-identical class string to the host's own outer SectionCard shell, creating a visual "card within the single host card" pattern for each of the 6 named sections (Essentials, What Gets Renamed, Run & Automation, Token Settings, Destination Routing, Advanced). Does not violate the SectionCard boundary contract (each `Panel`'s `<h2>` is a distinct section name, not a repeat of "Renamer"; `EXTENSION_UI.md` only documents a contract for the *outer* boundary, not internal sub-sectioning) | drift | Consider replacing `Panel` with nested `CollapsibleSection`/`GroupCard` usage (both already imported and used elsewhere in this same file, e.g. `RenameSettingsPanel.tsx:847-913,1123-1165`) for visual consistency — a recommendation for a future milestone, not an instruction being executed now |
| Live Preview sticky card (`RenameSettingsPanel.tsx:778`) | `rounded-2xl border border-border bg-surface p-5 lg:sticky lg:top-16` — same class string as `Panel` above | Same SectionCard geometry (`EXTENSION_UI.md:9-19`) | Same "card-in-card" visual pattern as the `Panel` finding above, applied to the live-preview column rather than a named settings section | drift | Group with the `Panel` finding above as the same structural observation — both are instances of reusing the exact SectionCard shell for internal organization; no separate fix needed beyond what's already noted |
| `SaveBar` fixed-bottom bar (`RenameSettingsPanel.tsx:330-372`) | `position:fixed` bottom bar (`fixed inset-x-0 bottom-0 z-50 border-t border-border bg-surface px-6 py-4`), visible only while `dirty` | No host equivalent exists in `COMPONENTS.md`'s inventory — no "global save bar" primitive is cataloged | Bespoke pattern with no canonical counterpart to converge on | n/a (bespoke) | No action needed — this is necessary UI Cove's own settings pages don't need (they presumably auto-save or lack a global dirty-state concept); no canonical equivalent exists to converge on |

### primitives.tsx — 20 locally-reimplemented form/UI primitives

The file's entire re-implementation strategy (`primitives.tsx:1-29` header comment) is documented, intentional, and correct — see Positive Findings below. The rows that follow are the genuine divergences/observations found within this otherwise-compliant file.

| Property | Renamer | Canonical | Divergence | Severity | Bring-it-in-line |
|---|---|---|---|---|---|
| `Toggle` (`primitives.tsx:431-467`) | Switch-style toggle (`role="switch"`, pill track + sliding knob) | Host has no switch-style `Toggle` — only `CheckboxLabel` (`SettingsPrimitives.tsx:274-286`), a checkbox | Renamer introduces a control shape (switch) the host's own settings-primitive set does not offer at all — not a "should match X's classes" case since no analogous host component exists | n/a (bespoke) | No action needed — Renamer's `Toggle` fills a gap in the host's own primitive set rather than diverging from an existing one |
| `GroupCard` (`primitives.tsx:1056-1085`): `rounded-xl border border-border bg-card p-4` | Sub-card for grouping fields within a `Panel`/`CollapsibleSection` | `InfoPair` (`SettingsPrimitives.tsx:288-295`) — loose match only; `InfoPair` is a read-only label/value display, not a general grouping container | Loose conceptual match, not a class-for-class equivalent; `GroupCard`'s actual purpose (a general content-grouping card) has no precise host analog | cosmetic | No action needed — the classes used (`rounded-xl`/`border-border`/`bg-card`/`p-4`) are all real tokens; the structural role is Renamer-specific with no closer host equivalent to converge on |
| `PrimaryButton`/`GhostButton` (`primitives.tsx:1132-1172`) | Two separate button components | `SettingsButton` (`SettingsPrimitives.tsx:363-372`) — ONE component with a `variant?: "primary" \| "danger" \| "ghost"` prop | Renamer splits into two components where the host uses one with a variant prop — a structural (API-shape) divergence, not a token divergence; both use compliant token classes (`bg-accent`, `border-border`, `bg-card`) | cosmetic | Consider consolidating into a single `Button` component with a variant prop to match the host's API shape more closely — a minor internal-consistency note, not a visual defect (no visible difference to the user) |
| `StatusText` (`primitives.tsx:1176-1186`) color mapping | `text-green-400` / `text-red-400` / `text-amber-400` / `text-secondary` (raw Tailwind default-palette colors for success/error/warning) | `DESIGN_SYSTEM.md:88-97`'s 14 semantic role tokens have no success/error/warning/status-color role | No canonical status-color token exists to converge on — this is a gap in the canonical system itself, not a Renamer defect | cosmetic | Frame as "no canonical status-color token exists" — no action owed to Renamer; if a future milestone formalizes status-color tokens in `DESIGN_SYSTEM.md`, this would become the convergence point |
| Chip pattern (`CHIP_BASE`/`CHIP_SELECTED`/`CHIP_UNSELECTED`, `primitives.tsx:53-66`) | `cursor-pointer rounded-lg border px-2 py-1 text-xs` base + token-only color sets | No single shared "Chip" component exists in `COMPONENTS.md`'s inventory to compare against; the same visual pattern recurs in `TokenLegend.tsx:79`, `RenameSettingsPanel.tsx:296` (`PresetRow`), and `RenameSettingsPanel.tsx:1133-1160`-region token-insert buttons | All token-only classes (no divergence from canonical tokens), but the identical chip pattern is hand-repeated across at least 3 files/locations with no single shared component — a DRY observation distinct from a token-divergence finding | drift | Consider extracting a shared `Chip` component used by `primitives.tsx`, `TokenLegend.tsx`, and `PresetRow` — an internal-consistency/DRY recommendation for a future milestone |

### dialog.tsx — custom Dialog modal shell + ErrorBox

| Property | Renamer | Canonical | Divergence | Severity | Bring-it-in-line |
|---|---|---|---|---|---|
| `Dialog` component (`dialog.tsx:21-101`) | Custom modal shell: `role="dialog"`, `aria-modal`, `aria-labelledby`, `aria-describedby`, focus trap (`dialog.tsx:55-82`), Esc-to-cancel (`dialog.tsx:57-60`), scrim-click-cancel (`dialog.tsx:88`); panel classes `rounded-lg border border-border bg-surface p-6 shadow-xl` | `ConfirmDialog` (`ConfirmDialog.tsx:21-99`) | Per the file's own header comment (`dialog.tsx:10-14`): `ConfirmDialog` "is not a swap" — it has no `role="dialog"`, no focus trap, no Esc-to-cancel, no scrim-click-cancel, no size variants, and is built for a single destructive-delete use case with a fixed `max-w-sm`. Renamer's `Dialog` is a documented, deliberate **superset** of host functionality (stronger accessibility), not a hand-rolled shortcut | drift (safe direction) | The "bring it in line" direction here is arguably reversed from the usual case: consider upstreaming Renamer's accessibility improvements (focus trap, Esc-to-cancel, scrim-click-cancel, ARIA `role="dialog"`) to the host's `ConfirmDialog` itself, rather than regressing Renamer's `Dialog` to match a less-accessible baseline |
| `ErrorBox` (`dialog.tsx:104-110`) | `rounded border border-red-700 bg-red-950/60 px-3 py-2 text-sm text-red-200` — matches `ConfirmDialog`'s own destructive error styling per the file's header comment | `ConfirmDialog`'s destructive error styling (same red-palette convention) | Uses Tailwind default-palette red shades (`red-700`, `red-950`, `red-200`), same class as `DESIGN_SYSTEM.md`'s documented gap (no formal error/status-color token) — consistent with the `StatusText` cosmetic finding above | cosmetic | No action owed to Renamer — matches the host's own convention for destructive error styling, and no canonical error-color token exists to converge on instead |

### PreviewCard.tsx — styled diff-preview card

| Property | Renamer | Canonical | Divergence | Severity | Bring-it-in-line |
|---|---|---|---|---|---|
| Card shell (`PreviewCard.tsx:40`): `rounded-xl border border-border bg-card p-4` | Real tokens throughout (`border-border` → `#2a2d38`, `bg-card` → `#1e2028` per `DESIGN_SYSTEM.md:16,17`) | Loose conceptual match to `InfoPair` (`SettingsPrimitives.tsx:288-295`) — no exact host equivalent for a diff-preview card | None — no hardcoded hex/rgba, no arbitrary bracket syntax; card-nesting depth (this card sits inside the Live Preview sticky card at `RenameSettingsPanel.tsx:778`, itself inside the two-pane grid, itself inside the single host SectionCard) is a layout observation, not a token violation | cosmetic | No action needed — informational only, cited for completeness per the audit's full-coverage requirement, not because a fix is owed |
| Flag advisory text color (`PreviewCard.tsx:60`): `text-amber-400` | Raw Tailwind default-palette amber for advisory flags (`empty`, `sanitized`, `length-reduced`, `gating-skip`) | Same gap as `StatusText`/`WarningBadge` — no canonical warning-color token exists | No canonical token to converge on — same documented system-level gap | cosmetic | No action owed to Renamer; same disposition as the `StatusText` finding above |

### WarningBadge.tsx — status pill badges

| Property | Renamer | Canonical | Divergence | Severity | Bring-it-in-line |
|---|---|---|---|---|---|
| `VARIANT_CLASS` (`WarningBadge.tsx:24-28`) | `amber: "border-amber-400/40 bg-amber-400/10 text-amber-400"`, `gray: "border-border bg-card text-muted"`, `red: "border-red-700/50 bg-red-950/40 text-red-400"` | `DESIGN_SYSTEM.md:13-28`'s 14 semantic role tokens have no success/error/warning/status-color role (confirmed — `background`, `surface`, `card`, `card-hover`, `border`, `input`, `foreground`, `secondary`, `muted`, `accent`, `accent-hover`, `overlay`, `nav`, `nav-active` only) | `amber-400`/`red-700` are Tailwind default-palette colors with no canonical role-token equivalent to converge on; `gray` variant already uses real tokens (`border-border`/`bg-card`/`text-muted`) — no divergence there | cosmetic | Frame as a gap in the canonical system, not a Renamer defect — `DESIGN_SYSTEM.md`'s "Known Issues" section (`DESIGN_SYSTEM.md:101-111`) does not currently list a status-color gap; a future design-system milestone could add one, at which point this finding would become the convergence target |

`WarningBadge.tsx`'s accessibility pattern (color is never the only signal — amber/red badges pair a glyph with text, `WarningBadge.tsx:63-64`) is a positive observation, not a divergence — see Positive Findings below.

### TokenLegend.tsx — token chip row

| Property | Renamer | Canonical | Divergence | Severity | Bring-it-in-line |
|---|---|---|---|---|---|
| Chip pattern repetition (see `primitives.tsx`'s DRY finding above) | Identical `rounded-lg border border-border bg-card px-2 py-1` shape reused verbatim in `TokenLegend.tsx:79`, `PresetRow` (`RenameSettingsPanel.tsx:296`), and the `chipClass()` helper in `primitives.tsx:53-66` | No single shared `Chip` component exists to compare against | Same DRY drift as noted under `primitives.tsx` — this file is one of the 3 repetition sites, not a separate finding | drift | See the consolidated `primitives.tsx` "consider a shared Chip component" note above — this is the same finding, not counted twice in the Summary Table |

`TokenLegend.tsx`'s own chip classes (`TokenLegend.tsx:79`) are fully token-compliant on their own (no hardcoded hex, no arbitrary bracket syntax) — see Positive Findings below.

### DryRunModal.tsx / UndoSection.tsx / entityPicker.tsx / studioMap.tsx — consumer group

`DryRunModal.tsx` (270 lines) consumes `Dialog`/`ErrorBox` from `dialog.tsx` (`DryRunModal.tsx:18`) and `GhostButton`/`PrimaryButton`/`Spinner` from `primitives.tsx` (`DryRunModal.tsx:19`) — introduces no new primitive. The already-audited findings in the `dialog.tsx` and `primitives.tsx` sections above apply; this file is a usage site, not a separate divergence source, so it contributes no new row here.

`studioMap.tsx` (108 lines) — `StudioDestinationsEditor`/`StudioKeyCell` bridges `KeyValueMapEditor` (from `primitives.tsx`) with `StudioPicker` (from `entityPicker.tsx`) via a numeric-key adapter (`studioMapLogic.ts`, excluded per the pure-.ts scope note). It introduces no new visual primitive — only compositional/adapter logic reusing already-audited components — so it likewise contributes no new row here.

| Property | Renamer | Canonical | Divergence | Severity | Bring-it-in-line |
|---|---|---|---|---|---|
| `UndoSection.tsx` (257 lines) inline destructive-confirm buttons | The destructive-confirm footer uses inline raw `<button>` elements (`UndoSection.tsx:232-251`, e.g. `bg-red-600 ... hover:bg-red-500`) rather than `PrimaryButton`/`GhostButton` from `primitives.tsx` (which the rest of the file does import and use, e.g. `UndoSection.tsx:15`) | Same primitives as the `dialog.tsx`/`primitives.tsx` findings above; the inline destructive-button styling uses raw Tailwind red-palette classes, same disposition as the `ErrorBox`/`StatusText` cosmetic findings | The inline destructive buttons don't reuse `PrimaryButton`/`GhostButton` — a minor internal-consistency note, not a token divergence (colors are real Tailwind classes, same "no canonical destructive-color token" gap as elsewhere) | cosmetic | Consider using a `PrimaryButton`-style component with a destructive variant instead of inline `<button>` markup, for consistency with the rest of the codebase's button usage — a minor internal-consistency recommendation, not a defect |
| `entityPicker.tsx` (369 lines) — `StudioPicker`/`TagPicker`/`PerformerPicker` | Searchable, async-backed picker: text input + filtered results panel + removable chips, built on `filterEntities`/`excludeEntities`/`resolveStudioLabel` (`entityPickerLogic.ts`) | No searchable async entity picker exists anywhere in `COMPONENTS.md`'s inventory — closest concept is `FilterDialog`'s criteria builder (`COMPONENTS.md:27`), a fundamentally different UI (a full-page criteria-builder dialog, not an inline typeahead picker) | Genuinely novel UI for this extension's domain — Cove core has no "pick a studio/tag/performer by search" widget outside its own internal list/filter pages | n/a (bespoke) | No action needed — bespoke, no host primitive exists to converge on; the component reuses `primitives.tsx`'s `Field`/`StatusText`/`Spinner` and the token-only chip/input classes verbatim, so its *styling* is fully compliant even though its *shape* has no host counterpart |

## Summary Table (all findings, severity-sorted: blocker → drift → cosmetic → n/a (bespoke))

**Zero blocker-tier findings exist in this codebase.** This is a confirmed, expected outcome (per `16-UI-SPEC.md`'s Severity Rubric worked example): `RenamePage.tsx` does not add an outer card or duplicate the host-rendered heading (see Surface-Level Contract Check above), and no other audited file introduces a second outer-SectionCard-equivalent wrapper at the host boundary. Stated explicitly as a confirmed non-finding, not omitted or manufactured.

| # | Component | Finding | Severity |
|---|---|---|---|
| 1 | `RenameSettingsPanel.tsx` | `Panel` sub-card shell duplicates SectionCard's exact class string (6 named sections + the Live Preview sticky card) | drift |
| 2 | `primitives.tsx` / `TokenLegend.tsx` / `RenameSettingsPanel.tsx` (PresetRow) | Selectable-chip class pattern repeated across 3+ locations with no shared `Chip` component (DRY drift) | drift |
| 3 | `dialog.tsx` | `Dialog` is an accessibility superset of host `ConfirmDialog` (safe-direction drift; host arguably should adopt Renamer's pattern) | drift |
| 4 | `primitives.tsx` | `PrimaryButton`/`GhostButton` split into two components vs. host's single `SettingsButton` with a variant prop | cosmetic |
| 5 | `primitives.tsx` | `GroupCard` is a loose conceptual match to `InfoPair` with no precise host equivalent | cosmetic |
| 6 | `primitives.tsx` / `PreviewCard.tsx` / `dialog.tsx` (ErrorBox) | `StatusText`/flag-advisory/`ErrorBox` color mappings use raw Tailwind palette colors (no canonical status/error-color token exists) | cosmetic |
| 7 | `WarningBadge.tsx` | `VARIANT_CLASS` amber/red use raw Tailwind palette colors (no canonical status-color token exists) | cosmetic |
| 8 | `UndoSection.tsx` | Inline destructive `<button>` markup instead of a shared button primitive | cosmetic |
| 9 | `PreviewCard.tsx` | Card-nesting depth (informational layout observation, no token violation) | cosmetic |
| 10 | `RenameSettingsPanel.tsx` | `SaveBar` fixed-bottom bar has no host equivalent to compare against | n/a (bespoke) |
| 11 | `primitives.tsx` | `Toggle` (switch-style) has no host equivalent — host only has a checkbox-style `CheckboxLabel` | n/a (bespoke) |
| 12 | `entityPicker.tsx` | `StudioPicker`/`TagPicker`/`PerformerPicker` — searchable async pickers have no host counterpart | n/a (bespoke) |

## Positive Findings

- **`RenamePage.tsx`** — confirmed PASS on the SectionCard heading-slot rule (see Surface-Level Contract Check); `RenamePage.tsx:13-15` returns `<RenamePanelBody />` with zero outer wrapping, matching `EXTENSION_UI.md:25`'s contract exactly.
- **`primitives.tsx`, `dialog.tsx`, `PreviewCard.tsx`, `TokenLegend.tsx`** — zero raw hex/rgba literals and zero arbitrary-value Tailwind bracket syntax found anywhere in these 4 files (independently re-confirmed this session). `primitives.tsx`'s `INPUT_CLASS` (`primitives.tsx:36-37`) and `TokenLegend.tsx`'s chip classes (`TokenLegend.tsx:79`) use only semantic-role tokens that resolve to real `DESIGN_SYSTEM.md:13-28` custom properties.
- **`primitives.tsx`'s entire re-implementation strategy** (header comment, `primitives.tsx:1-29`) is documented, intentional, and correct: every one of its 20 primitives (`Field`, `TextInput`, `NumberInput`, `Select`, `CollapsibleSection`, etc.) matches a host `SettingsPrimitives.tsx` counterpart's Tailwind class conventions rather than attempting an import — the only possible approach given `COMPONENTS.md:45-49`'s confirmed zero-visual-export finding (COMP-02).
- **`primitives.tsx`'s import audit** (header comment, lines 1-29) and **`dialog.tsx`'s import audit** (header comment, lines 10-14) are unusually disciplined, dated, self-documented rationale for every reimplementation decision — cited directly as this audit's evidentiary basis for "no core equivalent exists" claims throughout.
- **`WarningBadge.tsx`** — color is never the only signal; amber/red badges always pair a `lucide-react` `AlertTriangle` glyph with text (`WarningBadge.tsx:63-64`), an accessibility best practice unprompted by any canonical requirement.
