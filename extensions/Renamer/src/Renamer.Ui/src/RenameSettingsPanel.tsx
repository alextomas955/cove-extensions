/**
 * RenameSettingsPanel — the extension's settings + live-preview panel.
 *
 * Rendered by the host with NO props inside its own SectionCard, so the
 * panel ROOT is a plain <div> — no outer card, no page title.
 *
 * Persistence contract (verified against the host):
 *   - LOAD via `store.getAll()` then read the "options" key (GET /api/extensions/{id}/data;
 *     the per-key GET route does not exist on the host).
 *   - SAVE via PUT /api/extensions/{id}/data/options with a DOUBLE-encoded body: the host route
 *     binds `[FromBody] string value`, so the HTTP body must be a JSON string literal whose
 *     content is the options JSON → `JSON.stringify(JSON.stringify(options))`.
 *   - The PUT returns HTTP 200 with an EMPTY body; the SDK `request()` only short-circuits on
 *     204 and would call res.json() on the empty 200 → spurious SyntaxError. `saveOptions`
 *     tolerates that (an ApiError is a real failure; a JSON-parse error on a 2xx is success).
 *
 * Live preview (UI-02): a ~250ms-debounced POST to /preview-sample with the in-flight options;
 * the per-sample old→new + flags are rendered by <PreviewCard>. The panel never re-implements
 * naming — the backend engine is the single source of truth.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { AlertTriangle } from "lucide-react";
import { request, ApiError, useExtensionStore } from "@cove/extension-sdk";

import {
  type RenamerOptions,
  type MultiValueOptions,
  type CaseTransform,
  type OverflowPolicy,
  type SortOrder,
  type PathDestinationRule,
  type ExcludeRule,
  type FieldReplaceRule,
  cloneDefaults,
  normalizeOptions,
  extractUnmodeledFields,
} from "./options";
import {
  Field,
  TextInput,
  NumberInput,
  Select,
  Toggle,
  TagListInput,
  CollapsibleSection,
  GroupCard,
  SectionCard,
  SectionGroupHeader,
  ToggleHeaderCard,
  Badge,
  KeyValueMapEditor,
  ObjectArrayEditor,
  RegexValidity,
  PathShapeHint,
  Button,
  Chip,
  StatusText,
  Spinner,
  ExampleSelect,
  SeparatorChips,
  SegmentedReplace,
  TokenPicker,
  ChipMultiSelect,
  OrderedPickToAdd,
  type ExampleOption,
  type SeparatorOption,
} from "./primitives";
import { extensionShapeAdvisory } from "./primitivesLogic";
import { StudioPicker, TagPicker, PerformerPicker } from "./entityPicker";
import { type ValueOption } from "./entityPickerLogic";
import { StudioDestinationsEditor } from "./studioMap";
import { TokenLegend } from "./TokenLegend";
import { PreviewCard, type PreviewSampleResult } from "./PreviewCard";
import { UndoSection } from "./UndoSection";
import { DryRunModal } from "./DryRunModal";
import type { ScanItem } from "./preview";
import { countByStatus } from "./dryRunLogic";
import {
  bracesBalanced,
  unknownTokens,
  suggestFor,
  isKnownToken,
  BARE_TOKENS,
  templateUsesToken,
} from "./templateValidation";
import { PRESETS } from "./presets";

const EXTENSION_ID = "com.alextomas955.renamer";
const OPTIONS_KEY = "options";
const DATA_BASE = `/extensions/${EXTENSION_ID}/data`;
const PREVIEW_PATH = `/extensions/${EXTENSION_ID}/preview-sample`;
const RENAME_LIBRARY_PATH = `/extensions/${EXTENSION_ID}/renamer-library`;
const PREVIEW_DEBOUNCE_MS = 250;
const JOB_POLL_INTERVAL_MS = 1000;

/** Strip one leading dot if present, then lowercase — the add-time transform for a sidecar extension. */
function normalizeSidecarExtension(raw: string): string {
  let v = raw.trim();
  if (v.startsWith(".")) v = v.slice(1);
  return v.toLowerCase();
}

const CASE_OPTIONS: readonly { value: CaseTransform; label: string }[] = [
  { value: "None", label: "None" },
  { value: "Lower", label: "lower case" },
  { value: "Title", label: "Title Case" },
];
const OVERFLOW_OPTIONS: readonly { value: OverflowPolicy; label: string }[] = [
  { value: "DropAll", label: "Drop all when over the max" },
  { value: "KeepFirst", label: "Keep the first N" },
];
// Sort orders are not interchangeable between the two groups: the engine only honors id/favorite
// ordering for performers (tags fall back to name ordering), so a tag Sort that offered them would
// silently no-op. Hence two distinct lists rather than one shared constant.
const PERFORMER_SORT_OPTIONS: readonly { value: SortOrder; label: string }[] = [
  { value: "NameAsc", label: "Name (A→Z)" },
  { value: "None", label: "Keep original order" },
  { value: "IdAsc", label: "By internal id" },
  { value: "FavoriteFirst", label: "Favorites first, then name" },
];
const TAG_SORT_OPTIONS: readonly { value: SortOrder; label: string }[] = [
  { value: "NameAsc", label: "Name (A→Z)" },
  { value: "None", label: "Keep original order" },
];

// The fixed performer-gender set. The VALUE is the C# enum NAME the backend matches (case-insensitive);
// the label is the friendly spelling. Shared by the ignore-genders multiselect and the gender-order
// ranking, so both offer exactly the genders the engine understands rather than free text.
const GENDER_OPTIONS: readonly ValueOption[] = [
  { value: "Male", label: "Male" },
  { value: "Female", label: "Female" },
  { value: "TransgenderMale", label: "Transgender male" },
  { value: "TransgenderFemale", label: "Transgender female" },
  { value: "Intersex", label: "Intersex" },
  { value: "NonBinary", label: "Non-binary" },
];

// The 18 canonical token names a FieldReplaceRule may target, mirroring Engine/TemplateEngine.cs
// `Tokens`. The value is the canonical spelling the backend matches (case-insensitive); offering the
// closed set keeps a rule from targeting a token the engine never resolves.
const TOKEN_OPTIONS: readonly { value: string; label: string }[] = [
  "title",
  "studio",
  "parentStudio",
  "studioCode",
  "director",
  "bitrate",
  "date",
  "year",
  "height",
  "width",
  "resolution",
  "videoCodec",
  "audioCodec",
  "frameRate",
  "duration",
  "performers",
  "tags",
  "ext",
].map((t) => ({ value: t, label: t }));

// Common DateFormat options; the example column uses the reference date 2026-03-12.
const DATE_FORMAT_OPTIONS: readonly ExampleOption[] = [
  { value: "yyyy-MM-dd", example: "2026-03-12" },
  { value: "yyyy", example: "2026" },
  { value: "MM-dd-yyyy", example: "03-12-2026" },
  { value: "dd.MM.yyyy", example: "12.03.2026" },
  { value: "yyyy.MM.dd", example: "2026.03.12" },
];

// Common DurationFormat options; the example column uses the reference duration 1h 23m 45s.
// Values carry the engine's literal backslash escapes exactly (TS "hh\\-mm\\-ss" = literal hh\-mm\-ss).
const DURATION_FORMAT_OPTIONS: readonly ExampleOption[] = [
  { value: "hh\\-mm\\-ss", example: "01-23-45" },
  { value: "hh\\.mm\\.ss", example: "01.23.45" },
  { value: "mm\\-ss", example: "83-45" },
];

// Common separators; each label makes the literal whitespace visible.
const SEPARATOR_OPTIONS: readonly SeparatorOption[] = [
  { value: ", ", label: "Comma + space ( , )" },
  { value: " · ", label: "Middot ( · )" },
  { value: " ", label: "Space ( ␣ )" },
  { value: " - ", label: "Dash ( - )" },
];

// Common duplicate-suffix patterns; {n} = collision counter, shown via example.
const SUFFIX_FORMAT_OPTIONS: readonly ExampleOption[] = [
  { value: " ({n})", example: "name (1).mp4" },
  { value: "_{n}", example: "name_1.mp4" },
  { value: " - {n}", example: "name - 1.mp4" },
];

/**
 * Inline, advisory, NON-BLOCKING template validation. Renders 0..N amber lines
 * under a template field: one for unbalanced braces, one per unknown $token (with a best-effort
 * "Did you mean"), and — for the filename field — one per sample whose /preview-sample flags
 * include "empty" (passed in via emptySamples; reuses the existing debounced preview, no new
 * request). Renders nothing when there are no issues. NEVER feeds Save and never moves the caret.
 *
 * SECURITY: every string is a React text node (auto-escaped) — no raw-HTML rendering.
 * The "Did you mean" suggestion is derived from the static TOKENS set, never echoing user markup.
 */
function TemplateValidation({
  value,
  emptySamples = [],
}: {
  value: string;
  emptySamples?: string[];
}) {
  const lines: string[] = [];
  if (!bracesBalanced(value)) {
    lines.push("Unmatched { or } — it'll still render, but check your groups.");
  }
  for (const tok of unknownTokens(value)) {
    const suggestion = suggestFor(tok);
    lines.push(
      suggestion
        ? `${tok} isn't a known token — it'll render as empty. Did you mean ${suggestion}?`
        : `${tok} isn't a known token — it'll render as empty.`,
    );
  }
  for (const label of emptySamples) {
    lines.push(`This template produces an empty name for the "${label}" sample.`);
  }
  if (lines.length === 0) return null;
  return (
    <div className="mt-1 space-y-1" role="status" aria-live="polite">
      {lines.map((line) => (
        <p key={line} className="flex items-start gap-1 text-xs text-amber-400">
          <AlertTriangle className="h-3 w-3 shrink-0" />
          <span>{line}</span>
        </p>
      ))}
    </div>
  );
}

/** The "Run for the whole library" success/error banner state — mirrors UndoSection's Feedback shape. */
type RunLibraryFeedback =
  { kind: "success"; text: string } | { kind: "error"; text: string } | null;

/**
 * Inline, advisory, NON-BLOCKING invalid-token flagging for the bare-token fields
 * (RequiredFields / DropOrder). Renders one amber line per chip value that is NOT a known token
 * (with a best-effort "Did you mean" from the shared `suggestFor`, displayed as a bare name to
 * match these fields' format). Mirrors {@link TemplateValidation} exactly: it NEVER removes a chip,
 * NEVER blocks Save, and NEVER feeds the persisted shape — purely UX guidance. Renders nothing when
 * every value is a known token.
 *
 * SECURITY: every string is a React text node (auto-escaped); the suggestion is derived
 * from the static TOKENS set, never echoing user markup.
 */
function TokenAdvisory({ values }: { values: string[] }) {
  const lines: string[] = [];
  for (const value of values) {
    if (isKnownToken(value)) continue;
    const suggestion = suggestFor(value); // returns a `$`-prefixed name or undefined
    const bare = suggestion ? suggestion.slice(1) : undefined;
    lines.push(
      bare
        ? `"${value}" isn't a known token — it'll be ignored. Did you mean ${bare}?`
        : `"${value}" isn't a known token — it'll be ignored.`,
    );
  }
  if (lines.length === 0) return null;
  return (
    <div className="mt-1 space-y-1" role="status" aria-live="polite">
      {lines.map((line) => (
        <p key={line} className="flex items-start gap-1 text-xs text-amber-400">
          <AlertTriangle className="h-3 w-3 shrink-0" />
          <span>{line}</span>
        </p>
      ))}
    </div>
  );
}

/**
 * One-click starter templates. Each chip sets FilenameTemplate via the parent's
 * set() path so `dirty` flips and the existing debounced live preview re-renders — no toast, no
 * confirm. Chips reuse the legend-chip class (prose labels drop font-mono). Every preset label is a
 * React text node (auto-escaped); the templates come from the static PRESETS list.
 */
function PresetRow({ onApply }: { onApply: (filenameTemplate: string) => void }) {
  return (
    <div>
      <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">
        Presets
      </span>
      <div className="flex flex-wrap gap-1">
        {PRESETS.map((p) => (
          <Chip
            key={p.label}
            selected={false}
            title={p.filenameTemplate}
            onClick={() => {
              onApply(p.filenameTemplate);
            }}
          >
            {p.label}
          </Chip>
        ))}
      </div>
      <p className="mt-1 text-xs text-muted">
        Click a preset to fill the filename template. You can edit it afterwards.
      </p>
    </div>
  );
}

/**
 * The fixed-bottom global save bar — reachable from anywhere on the page, visible only while
 * `dirty`. Reuses the existing dirty/saving/saveError/savedFlash state and onSave handler verbatim;
 * Discard reverts to the last-saved snapshot (`saved`), never the factory defaults.
 */
function SaveBar({
  dirty,
  saving,
  saveError,
  savedFlash,
  canSave,
  onSave,
  onDiscard,
}: {
  dirty: boolean;
  saving: boolean;
  saveError: string | null;
  savedFlash: boolean;
  canSave: boolean;
  onSave: () => void;
  onDiscard: () => void;
}) {
  if (!dirty) return null;
  return (
    <div className="pointer-events-none fixed inset-x-0 bottom-0 z-50 flex justify-center px-4 py-4">
      {/* py-3.5 is host-absent (Cove's own UI never uses it, so its prebuilt bundle omits it);
          inline the 0.875rem padding instead of shipping CSS. */}
      <div
        className="pointer-events-auto flex w-full max-w-3xl items-center gap-4 rounded-2xl border border-border bg-card px-5 shadow-lg"
        style={{ paddingTop: "0.875rem", paddingBottom: "0.875rem" }}
      >
        <span
          className={`h-2 w-2 shrink-0 rounded-full ${
            saveError ? "bg-red-400" : savedFlash ? "bg-green-400" : "bg-amber-400"
          }`}
        />
        <div className="min-w-0 flex-1">
          {saveError ? (
            <StatusText kind="error">
              Couldn't save settings — {saveError}. Your changes are still here; try Save again.
            </StatusText>
          ) : savedFlash ? (
            <StatusText kind="success">Settings saved.</StatusText>
          ) : (
            <>
              <div className="text-sm font-semibold text-foreground">Unsaved changes</div>
              <div className="mt-0.5 text-xs text-secondary">
                Nothing on disk changes until you save. Running a rename requires saving first.
              </div>
            </>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-3">
          <Button variant="ghost" onClick={onDiscard} disabled={saving}>
            Discard
          </Button>
          <Button onClick={onSave} disabled={!canSave || saving}>
            {saving ? <Spinner /> : null}
            Save changes
          </Button>
        </div>
      </div>
    </div>
  );
}

/**
 * Save the options blob. Tolerates the host's empty-200 response (see file header). Rethrows a
 * real ApiError so the caller can surface it; treats a JSON-parse error on a successful response
 * as success.
 *
 * `extras` carries any stored keys this panel does not model (backend-only settings such as the
 * path-routing fields). They are merged back ahead of the modeled options — modeled values always
 * win — so saving from this panel never erases configuration it cannot edit.
 */
async function saveOptions(
  options: RenamerOptions,
  extras: Record<string, unknown>,
): Promise<void> {
  const payload = { ...extras, ...options };
  try {
    await request<unknown>(`${DATA_BASE}/${OPTIONS_KEY}`, {
      method: "PUT",
      // Double-encode: inner serialize = the stored value; outer serialize makes it a JSON
      // string literal for the [FromBody] string binder.
      body: JSON.stringify(JSON.stringify(payload)),
    });
  } catch (err) {
    if (err instanceof ApiError) throw err; // genuine HTTP failure
    // Otherwise: res.ok was true but res.json() failed on the empty 200 body → success.
  }
}

/**
 * RenamePanelBody — the full rename config + live-preview UI (all state, load/save/preview logic,
 * and the two-pane JSX). This is the single shared body rendered by BOTH the Extensions-tab settings
 * section (via the thin `RenameSettingsPanel` wrapper) AND the dedicated nav page (`RenamePage`), so
 * the two homes never diverge. The root stays a plain `<div className="space-y-6">`;
 * the host SectionCard / page wrapper supplies outer chrome.
 */
export function RenamePanelBody() {
  const store = useExtensionStore(EXTENSION_ID);

  const [options, setOptions] = useState<RenamerOptions>(() => cloneDefaults());
  const [saved, setSaved] = useState<RenamerOptions>(() => cloneDefaults());
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedFlash, setSavedFlash] = useState(false);
  const [firstRun, setFirstRun] = useState(false);
  // Set when a stored blob could not be parsed and we fell back to defaults. Non-blocking: the panel
  // still renders so a Save rewrites a clean blob and clears the bad data.
  const [recoveredFromBadBlob, setRecoveredFromBadBlob] = useState(false);

  // Live (not-yet-committed) AssociatedExtensions input, so the sidecar-extension advisory reflects
  // what the user is currently typing, before Enter commits it.
  const [sidecarLiveInput, setSidecarLiveInput] = useState("");

  // Stored keys this panel does not model (backend-only settings, e.g. path routing). Captured on a
  // successful load and merged back on Save so editing here never erases them.
  const preservedExtras = useRef<Record<string, unknown>>({});

  const [preview, setPreview] = useState<PreviewSampleResult[] | null>(null);
  const [previewError, setPreviewError] = useState(false);

  // Last-focused template input, so a token chip inserts at its caret.
  const filenameRef = useRef<HTMLInputElement>(null);
  const folderRef = useRef<HTMLInputElement>(null);
  const activeTemplateRef = useRef<"filename" | "folder">("filename");

  const dirty = JSON.stringify(options) !== JSON.stringify(saved);
  // After recovering from an unreadable blob, defaults match `saved` so nothing looks "dirty" — but a
  // Save is still needed to overwrite the bad stored data, so allow it explicitly.
  const canSave = dirty || recoveredFromBadBlob;

  // ── Run for the whole library (Dry Run / Rename All) ────────────────────
  const [dryRunOpen, setDryRunOpen] = useState(false);
  const [renamingLibrary, setRenamingLibrary] = useState(false);
  const [runLibraryFeedback, setRunLibraryFeedback] = useState<RunLibraryFeedback>(null);

  /**
   * Polls `GET /jobs/{jobId}` until it leaves pending/running; rejects on failed/cancelled. The
   * host's minimal-API JSON options apply JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
   * which lowercases the leading character of the JobStatus enum's PascalCase member names
   * (Completed -> "completed") — the status strings here must be camelCase, not PascalCase.
   */
  function pollJobToCompletion(jobId: string): Promise<void> {
    return new Promise((resolve, reject) => {
      const interval = setInterval(() => {
        request<{ status: string; error?: string | null }>(`/jobs/${jobId}`)
          .then((job) => {
            if (job.status === "completed") {
              clearInterval(interval);
              resolve();
            } else if (job.status === "failed" || job.status === "cancelled") {
              clearInterval(interval);
              reject(new Error(job.error ?? "the job did not complete"));
            }
          })
          .catch(() => {
            // Transient poll failure — keep polling; a real failure surfaces via job.status.
          });
      }, JOB_POLL_INTERVAL_MS);
    });
  }

  /**
   * The SHARED "Rename all files" handler — called identically by the panel-level button and
   * the Dry Run modal's footer button. Enqueues the rename-library job, polls it to completion the
   * same way the modal polls its scan job, and reports renamed/skipped counts.
   *
   * The rename job itself never reports per-status counts (RunRenameLibraryJobAsync only calls
   * progress.Report(percent, message), no UnitsSucceeded/Summary), so the banner's counts come from
   * a scan: the modal already has one in memory (`scanItems` supplied), while the panel-direct path
   * has none yet and runs one first — both paths execute the SAME server-derived id set either way,
   * since the scan and the rename job independently call the identical LoadAllEntityIdsAsync query.
   */
  const renameLibrary = useCallback(async (scanItems?: ScanItem[]) => {
    setRenamingLibrary(true);
    setRunLibraryFeedback(null);
    try {
      let items = scanItems;
      if (!items) {
        const { jobId: scanJobId } = await request<{ jobId: string }>(
          `/extensions/${EXTENSION_ID}/scan-library`,
          { method: "POST" },
        );
        await pollJobToCompletion(scanJobId);
        items = await request<ScanItem[]>(`/extensions/${EXTENSION_ID}/last-scan`);
      }
      const counts = countByStatus(items);

      const { jobId } = await request<{ jobId: string }>(RENAME_LIBRARY_PATH, { method: "POST" });
      await pollJobToCompletion(jobId);

      setDryRunOpen(false);
      setRunLibraryFeedback({
        kind: "success",
        text:
          `Renamed ${counts.renamed} file${counts.renamed === 1 ? "" : "s"}` +
          (counts.skipped > 0 ? `, ${counts.skipped} skipped` : "") +
          `.`,
      });
    } catch (err) {
      const text = err instanceof ApiError ? `${err.status} ${err.body}` : String(err);
      setRunLibraryFeedback({
        kind: "error",
        text: `Couldn't rename — ${text}. Nothing was changed; you can try again.`,
      });
    } finally {
      setRenamingLibrary(false);
    }
  }, []);

  // ── Load on mount ──────────────────────────────────────────────────────
  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    setRecoveredFromBadBlob(false);
    try {
      const all = await store.getAll();
      // getAll() is typed Record<string,string>, but a MISSING key is `undefined` at runtime
      // (the index signature doesn't model that). Annotate the possibly-undefined reality so the
      // null/empty guard below stays meaningful rather than being treated as dead by the type.
      const blob: string | undefined = all[OPTIONS_KEY];
      if (!blob) {
        // missing key (undefined) or empty stored blob → first run
        setFirstRun(true);
        preservedExtras.current = {};
        const d = cloneDefaults();
        setOptions(d);
        setSaved(d);
      } else {
        setFirstRun(false);
        // Parse defensively. A blob written by an older version (or hand-edited) can be invalid JSON
        // — e.g. a value with single backslashes that aren't valid JSON escapes. Rather than blocking
        // the whole panel, fall back to defaults and flag it; the next Save rewrites a clean blob.
        let raw: unknown;
        try {
          raw = JSON.parse(blob);
        } catch {
          preservedExtras.current = {};
          const d = cloneDefaults();
          setOptions(d);
          setSaved(d);
          setRecoveredFromBadBlob(true);
          return;
        }
        // Keep any stored keys this panel does not model (backend-only settings) so Save preserves them.
        preservedExtras.current = extractUnmodeledFields(raw);
        // normalizeOptions rebuilds a clean canonical RenamerOptions, DROPPING any stale camelCase
        // duplicate keys a legacy blob may carry (the /preview-sample dual-source fix). The old spread
        // merge preserved them, so they overwrote live edits in the preview body. Because `options`
        // state is now canonical by construction, both the preview body and saveOptions are single-source
        // automatically, and the stored blob self-heals on the next Save.
        const parsed = normalizeOptions(raw);
        // A gate stored false whose underlying data is already non-empty must still surface
        // as ON, so an existing configuration is never silently hidden behind a new gate. Both
        // setOptions and setSaved get the identical derived value — using parsed for one and this
        // for the other would make the panel dirty on load for any such existing configuration.
        const withDerivedGates: RenamerOptions = {
          ...parsed,
          EnableStudioDestinations:
            parsed.EnableStudioDestinations || Object.keys(parsed.StudioDestinations).length > 0,
          EnableTagDestinations:
            parsed.EnableTagDestinations || Object.keys(parsed.TagDestinations).length > 0,
          EnableAdvancedRouting:
            parsed.EnableAdvancedRouting ||
            parsed.AllowedRoots.length > 0 ||
            parsed.PathDestinations.length > 0,
        };
        setOptions(withDerivedGates);
        setSaved(withDerivedGates);
      }
    } catch (err) {
      setLoadError(err instanceof ApiError ? `${err.status} ${err.body}` : String(err));
    } finally {
      setLoading(false);
    }
  }, [store]);

  useEffect(() => {
    // Data fetch on mount: load() awaits the store then setState()s the result — the canonical
    // "synchronize with an external system" effect, which the react-compiler heuristic can't see
    // through the async hop.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void load();
  }, [load]);

  // ── Debounced live preview ─────────────────────────────────────────────
  useEffect(() => {
    if (loading) return;
    const handle = setTimeout(() => {
      request<PreviewSampleResult[]>(PREVIEW_PATH, {
        method: "POST",
        body: JSON.stringify({ Options: options }),
      })
        .then((res) => {
          setPreview(res);
          setPreviewError(false);
        })
        .catch(() => {
          // Keep the last good preview; just flag that this refresh failed.
          setPreviewError(true);
        });
    }, PREVIEW_DEBOUNCE_MS);
    return () => {
      clearTimeout(handle);
    };
  }, [options, loading]);

  // ── Save ───────────────────────────────────────────────────────────────
  async function onSave() {
    setSaving(true);
    setSaveError(null);
    try {
      await saveOptions(options, preservedExtras.current);
      setSaved(options);
      setFirstRun(false);
      setRecoveredFromBadBlob(false);
      setSavedFlash(true);
      setTimeout(() => {
        setSavedFlash(false);
      }, 3000);
    } catch (err) {
      setSaveError(err instanceof ApiError ? `${err.status} ${err.body}` : String(err));
    } finally {
      setSaving(false);
    }
  }

  function set<K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) {
    setOptions((o) => ({ ...o, [key]: value }));
  }
  function setMulti(group: "Performers" | "Tags", patch: Partial<MultiValueOptions>) {
    setOptions((o) => ({ ...o, [group]: { ...o[group], ...patch } }));
  }

  function insertToken(token: string) {
    const which = activeTemplateRef.current;
    const el = which === "folder" ? folderRef.current : filenameRef.current;
    const key: "FilenameTemplate" | "FolderTemplate" =
      which === "folder" ? "FolderTemplate" : "FilenameTemplate";
    const current = options[key];
    if (el && typeof el.selectionStart === "number") {
      const start = el.selectionStart;
      const end = el.selectionEnd ?? start;
      const next = current.slice(0, start) + token + current.slice(end);
      set(key, next);
      requestAnimationFrame(() => {
        el.focus();
        const caret = start + token.length;
        el.setSelectionRange(caret, caret);
      });
    } else {
      set(key, current + token);
    }
  }

  if (loading) {
    return (
      <div className="flex items-center gap-2 text-sm text-secondary">
        <Spinner />
        Loading settings…
      </div>
    );
  }

  if (loadError) {
    return (
      <div className="space-y-3">
        <StatusText kind="error">
          Couldn't load your saved settings — {loadError}. Retry, or continue with defaults below.
        </StatusText>
        <div>
          <Button variant="ghost" onClick={() => void load()}>
            Retry
          </Button>
        </div>
      </div>
    );
  }

  const mv = (group: "Performers" | "Tags") => options[group];

  // Empty-for-sample advisory: read the existing debounced /preview-sample
  // result; name each sample whose flags include "empty". No new request.
  const emptySamples = (preview ?? [])
    .filter((r) => r.flags.includes("empty"))
    .map((r) => r.sampleLabel);

  const usesPerformers = templateUsesToken(
    "performers",
    options.FilenameTemplate,
    options.FolderTemplate,
  );
  const usesTags = templateUsesToken("tags", options.FilenameTemplate, options.FolderTemplate);
  const usesDate = templateUsesToken("date", options.FilenameTemplate, options.FolderTemplate);
  const usesDuration = templateUsesToken(
    "duration",
    options.FilenameTemplate,
    options.FolderTemplate,
  );

  // pb-20 (5rem bottom clearance for the sticky save bar) is host-absent — inline it.
  return (
    <div className="space-y-6" style={dirty ? { paddingBottom: "5rem" } : undefined}>
      {/* Two-pane shell, narrowed to the Filename & destination card only: the panel (2/3 via
        col-span-2) + the live preview (1/3) sticky on lg+. The other 5 panels render as full-width
        siblings below this grid, so the preview's sticky containing block is that first card's own
        height, not the whole page. Standard grid-cols-3 + col-span-2 only — the host Tailwind never
        compiles arbitrary [..] values for this bundle (verified live; check-classes enforces). */}
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
        <div className="col-span-2">
          {recoveredFromBadBlob ? (
            <StatusText kind="error">
              Your saved settings couldn't be read and have been reset to defaults. Review the
              options below and save to store a clean copy.
            </StatusText>
          ) : firstRun ? (
            <StatusText kind="muted">
              Using default settings — pick a preset or write a template, then save.
            </StatusText>
          ) : null}

          <SectionCard
            title="Filename & destination"
            description="Set how files are named and where they go — pick a preset or write your own template."
          >
            <PresetRow
              onApply={(t) => {
                set("FilenameTemplate", t);
              }}
            />
            <Field label="Filename template">
              <TextInput
                value={options.FilenameTemplate}
                onChange={(v) => {
                  set("FilenameTemplate", v);
                }}
                onFocus={() => (activeTemplateRef.current = "filename")}
                inputRef={filenameRef}
                mono
                placeholder="$title"
              />
            </Field>
            <TemplateValidation value={options.FilenameTemplate} emptySamples={emptySamples} />
            <TokenLegend onInsert={insertToken} />

            <div className="border-t border-border pt-4">
              <div className="text-base font-semibold text-foreground">Where files go</div>
              <p className="mb-4 mt-1 text-sm text-secondary">
                Folder path template — moves files on rename.
              </p>
              <Field
                label="Folder template"
                helper="Blank = no folder move (rename in place). Use / for sub-folders, e.g. $studio / $year."
              >
                <TextInput
                  value={options.FolderTemplate}
                  onChange={(v) => {
                    set("FolderTemplate", v);
                  }}
                  onFocus={() => (activeTemplateRef.current = "folder")}
                  inputRef={folderRef}
                  mono
                  placeholder="$studio / $year"
                />
              </Field>
              <TemplateValidation value={options.FolderTemplate} />
            </div>
          </SectionCard>
        </div>

        {/* ── LIVE PREVIEW (right, 1/3) — sticky under the 64px navbar (top-16) so it stays the
            visible centerpiece while scrolling the Filename & destination card. The COLUMN stretches to the row
            height (default align-items:stretch — do NOT use self-start, which collapses the column to
            content height and defeats position:sticky); the inner CARD is the sticky element. ── */}
        <div>
          <div className="space-y-4 rounded-2xl border border-border bg-surface p-5 shadow-sm lg:sticky lg:top-16">
            <div className="text-base font-semibold text-foreground">Live preview</div>
            <p className="mb-4 mt-1 text-sm text-secondary">
              Old → new for sample items, before anything touches disk.
            </p>
            {previewError ? (
              <StatusText kind="error">Preview unavailable — saved naming still works.</StatusText>
            ) : null}
            {preview == null ? (
              <div className="flex items-center gap-2 text-sm text-secondary">
                <Spinner />
                Rendering preview…
              </div>
            ) : (
              <div className="space-y-3">
                {preview.map((r) => (
                  <PreviewCard key={r.sampleLabel} result={r} />
                ))}
              </div>
            )}
          </div>
        </div>
      </div>

      <SectionGroupHeader title="What gets renamed" hint="Scope and required fields" />
      <SectionCard>
        <Toggle
          label="Only rename organized items"
          checked={options.OnlyOrganized}
          onChange={(v) => {
            set("OnlyOrganized", v);
          }}
          helper="Only rename items you've marked Organized — skips un-curated items so they don't get junk names."
        />
        <Toggle
          label="Use filename as title when none is set"
          checked={options.FilenameAsTitle}
          onChange={(v) => {
            set("FilenameAsTitle", v);
          }}
          helper="When an item has no title, use its current filename (without extension) as the title."
        />
        <Field
          label="Required fields"
          helper="Items whose listed tokens resolve to nothing are skipped instead of renamed. Default: title."
        >
          <TagListInput
            values={options.RequiredFields}
            onChange={(v) => {
              set("RequiredFields", v);
            }}
            placeholder="Add token, press Enter"
          />
          <TokenPicker
            tokens={BARE_TOKENS}
            values={options.RequiredFields}
            onAdd={(name) => {
              set(
                "RequiredFields",
                options.RequiredFields.includes(name)
                  ? options.RequiredFields
                  : [...options.RequiredFields, name],
              );
            }}
          />
          <TokenAdvisory values={options.RequiredFields} />
        </Field>
      </SectionCard>

      <SectionGroupHeader title="Run & automation" hint="When renames happen" />
      <SectionCard>
        <Toggle
          label="Auto-rename on update"
          checked={options.AutoRenamerOnUpdate}
          onChange={(v) => {
            set("AutoRenamerOnUpdate", v);
          }}
          helper="When on, renames a video or image automatically whenever its metadata changes — respects every gating and routing rule below. The core of a hands-off library. Off by default."
        />
        <div className="border-t border-border pt-4">
          <div className="flex flex-wrap items-start gap-4">
            <div className="min-w-0 flex-1">
              <div className="text-base font-semibold text-foreground">
                Run for the whole library
              </div>
              <p className="mt-1 text-sm text-secondary">
                Apply your rules to every matching item now. Preview first with a dry run — it
                writes nothing.
              </p>
            </div>
            <div className="flex shrink-0 flex-wrap items-center gap-3">
              <Button
                variant="ghost"
                onClick={() => {
                  setDryRunOpen(true);
                }}
              >
                Dry run
              </Button>
              <Button onClick={() => void renameLibrary()} disabled={dirty || renamingLibrary}>
                {renamingLibrary ? <Spinner /> : null}
                Rename all files
              </Button>
            </div>
          </div>
          {dirty ? (
            <p
              className="mt-2 flex items-start gap-1 text-xs text-amber-400"
              role="status"
              aria-live="polite"
            >
              <AlertTriangle className="h-3 w-3 shrink-0" />
              <span>
                Dry run previews your edits before saving. Save before &ldquo;Rename all
                files&rdquo; to run them for real.
              </span>
            </p>
          ) : null}
          {runLibraryFeedback ? (
            <p className="mt-2">
              <StatusText kind={runLibraryFeedback.kind}>
                {runLibraryFeedback.kind === "success" ? "✓ " : ""}
                {runLibraryFeedback.text}
                {runLibraryFeedback.kind === "success" ? (
                  <>
                    {" "}
                    <button
                      type="button"
                      onClick={() => {
                        document
                          .getElementById("rename-undo-section")
                          ?.scrollIntoView({ behavior: "smooth" });
                      }}
                      // No underline: hover:no-underline is host-absent (and hover:underline
                      // is too), and an inline style can't express a :hover state. The accent
                      // color alone marks it as the actionable control.
                      className="text-accent"
                    >
                      Undo
                    </button>
                  </>
                ) : null}
              </StatusText>
            </p>
          ) : null}
        </div>
      </SectionCard>

      {dryRunOpen ? (
        <DryRunModal
          options={options}
          onClose={() => {
            setDryRunOpen(false);
          }}
          onRenameAll={(items) => void renameLibrary(items)}
          renaming={renamingLibrary}
        />
      ) : null}

      <SectionGroupHeader title="Token settings" hint="Appear only for tokens you're using" />
      <div className="space-y-4">
        {usesPerformers ? (
          <SectionCard title="Performers" badge={<Badge mono>$performers</Badge>} accent>
            <Field label="Separator">
              <SeparatorChips
                value={mv("Performers").Separator}
                onChange={(v) => {
                  setMulti("Performers", { Separator: v });
                }}
                options={SEPARATOR_OPTIONS}
                customPlaceholder="Custom separator"
              />
            </Field>
            <Field label="Max count" helper="0 = unlimited">
              <NumberInput
                value={mv("Performers").MaxCount}
                min={0}
                onChange={(v) => {
                  setMulti("Performers", { MaxCount: v });
                }}
              />
            </Field>
            <Field label="On overflow">
              <Select
                value={mv("Performers").OnOverflow}
                onChange={(v) => {
                  setMulti("Performers", { OnOverflow: v });
                }}
                options={OVERFLOW_OPTIONS}
              />
            </Field>
            <Field label="Sort" helper="The id and favorite orders apply to performers only.">
              <Select
                value={mv("Performers").Sort}
                onChange={(v) => {
                  setMulti("Performers", { Sort: v });
                }}
                options={PERFORMER_SORT_OPTIONS}
              />
            </Field>
            <Field
              label="Ignore genders"
              helper="Drop performers of these genders before the max-count limit. A performer with no gender is always kept. None selected = off."
            >
              <ChipMultiSelect
                options={GENDER_OPTIONS}
                values={mv("Performers").IgnoreGenders}
                onChange={(v) => {
                  setMulti("Performers", { IgnoreGenders: v });
                }}
              />
            </Field>
            <Field
              label="Gender order"
              helper="Preferred gender order, most-preferred first. Empty = off."
            >
              <OrderedPickToAdd
                options={GENDER_OPTIONS}
                values={mv("Performers").GenderOrder}
                onChange={(v) => {
                  setMulti("Performers", { GenderOrder: v });
                }}
                addPrompt="Add a gender…"
              />
            </Field>
            <PerformerPicker
              label="Whitelist"
              helper="If set, only these performers are kept (case-insensitive)."
              values={mv("Performers").Whitelist}
              onChange={(v) => {
                setMulti("Performers", { Whitelist: v });
              }}
              placeholder="Search performers…"
            />
            <PerformerPicker
              label="Blacklist"
              helper="These performers are removed (case-insensitive)."
              values={mv("Performers").Blacklist}
              onChange={(v) => {
                setMulti("Performers", { Blacklist: v });
              }}
              placeholder="Search performers…"
            />
          </SectionCard>
        ) : null}

        {usesTags ? (
          <SectionCard title="Tags" badge={<Badge mono>$tags</Badge>} accent>
            <Field label="Separator">
              <SeparatorChips
                value={mv("Tags").Separator}
                onChange={(v) => {
                  setMulti("Tags", { Separator: v });
                }}
                options={SEPARATOR_OPTIONS}
                customPlaceholder="Custom separator"
              />
            </Field>
            <Field label="Max count" helper="0 = unlimited">
              <NumberInput
                value={mv("Tags").MaxCount}
                min={0}
                onChange={(v) => {
                  setMulti("Tags", { MaxCount: v });
                }}
              />
            </Field>
            <Field label="On overflow">
              <Select
                value={mv("Tags").OnOverflow}
                onChange={(v) => {
                  setMulti("Tags", { OnOverflow: v });
                }}
                options={OVERFLOW_OPTIONS}
              />
            </Field>
            <Field label="Sort">
              <Select
                value={mv("Tags").Sort}
                onChange={(v) => {
                  setMulti("Tags", { Sort: v });
                }}
                options={TAG_SORT_OPTIONS}
              />
            </Field>
            <TagPicker
              label="Whitelist"
              helper="If set, only these tags are kept (case-insensitive)."
              values={mv("Tags").Whitelist}
              onChange={(v) => {
                setMulti("Tags", { Whitelist: v });
              }}
              placeholder="Search tags…"
            />
            <TagPicker
              label="Blacklist"
              helper="These tags are removed (case-insensitive)."
              values={mv("Tags").Blacklist}
              onChange={(v) => {
                setMulti("Tags", { Blacklist: v });
              }}
              placeholder="Search tags…"
            />
          </SectionCard>
        ) : null}

        {usesDate || usesDuration ? (
          <SectionCard
            accent
            badge={
              <Badge mono>
                {usesDate && usesDuration ? "$date · $duration" : usesDate ? "$date" : "$duration"}
              </Badge>
            }
            title={
              usesDate && usesDuration
                ? "Date & duration format"
                : usesDate
                  ? "Date format"
                  : "Duration format"
            }
          >
            {usesDate ? (
              <Field label="Date format" helper="e.g. yyyy-MM-dd">
                <ExampleSelect
                  value={options.DateFormat}
                  onChange={(v) => {
                    set("DateFormat", v);
                  }}
                  options={DATE_FORMAT_OPTIONS}
                  customPlaceholder="yyyy-MM-dd"
                />
              </Field>
            ) : null}
            {usesDuration ? (
              <Field label="Duration format">
                <ExampleSelect
                  value={options.DurationFormat}
                  onChange={(v) => {
                    set("DurationFormat", v);
                  }}
                  options={DURATION_FORMAT_OPTIONS}
                  customPlaceholder="hh\-mm\-ss"
                />
              </Field>
            ) : null}
          </SectionCard>
        ) : null}

        {!usesPerformers && !usesTags && !usesDate && !usesDuration ? (
          <div className="rounded-xl border border-border bg-card p-6 text-center">
            <h3 className="text-base font-semibold text-foreground">
              No token-specific settings needed
            </h3>
            <p className="mx-auto mb-4 mt-1 max-w-md text-sm text-secondary">
              Add $performers, $tags, $date, or $duration to your filename or folder template to
              configure how they&apos;re formatted.
            </p>
            <div className="flex flex-wrap justify-center gap-1">
              <Chip
                selected={false}
                mono
                onClick={() => {
                  insertToken("{ - $performers}");
                }}
              >
                $performers
              </Chip>
              <Chip
                selected={false}
                mono
                onClick={() => {
                  insertToken("{ - $tags}");
                }}
              >
                $tags
              </Chip>
              <Chip
                selected={false}
                mono
                onClick={() => {
                  insertToken("{ - $date}");
                }}
              >
                $date
              </Chip>
              <Chip
                selected={false}
                mono
                onClick={() => {
                  insertToken("{ [$duration]}");
                }}
              >
                $duration
              </Chip>
            </div>
          </div>
        ) : null}
      </div>

      <SectionGroupHeader title="Destination routing" hint="Where renamed files land" />
      {/* Destination routing — where matched items move to. Card ORDER follows the design mock
            (default & unorganized first, then per-studio, per-tag, advanced routing & safety, then
            sidecar and empty-folder). This is presentation order only: it does NOT set the engine's
            rule-evaluation precedence, which is decided server-side, so reordering these cards is
            safe. Each per-* card carries its enable toggle in its header (ToggleHeaderCard); all
            fields flow through set() like every other control. */}
      <div className="space-y-4">
        <GroupCard title="Default & unorganized destinations">
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <Field
              label="Default destination"
              helper="Where an item matching no rule goes. Blank = no default route. Honored only with the relocate gate below ON."
            >
              <TextInput
                value={options.DefaultDestination}
                onChange={(v) => {
                  set("DefaultDestination", v);
                }}
                placeholder="Absolute root, or blank"
              />
              <PathShapeHint value={options.DefaultDestination} />
            </Field>
            <Field
              label="Unorganized destination"
              helper="Where un-curated items route instead of being skipped. Blank = no unorganized route."
            >
              <TextInput
                value={options.UnorganizedDestination}
                onChange={(v) => {
                  set("UnorganizedDestination", v);
                }}
                placeholder="Absolute root, or blank"
              />
              <PathShapeHint value={options.UnorganizedDestination} />
            </Field>
          </div>
          <Toggle
            label="Relocate unmatched items to the default destination"
            checked={options.EnableDefaultRelocate}
            onChange={(v) => {
              set("EnableDefaultRelocate", v);
            }}
            helper="With this on, any item matching no rule is moved to the default destination — whole-library reach. Undo is the only recovery. Off by default."
          />
        </GroupCard>

        <ToggleHeaderCard
          title="Per-studio destinations"
          description="Pick a studio, then the absolute root its items route to."
          enabled={options.EnableStudioDestinations}
          onToggle={(v) => {
            set("EnableStudioDestinations", v);
          }}
        >
          <StudioDestinationsEditor
            map={options.StudioDestinations}
            onChange={(m) => {
              set("StudioDestinations", m);
            }}
          />
        </ToggleHeaderCard>

        <ToggleHeaderCard
          title="Per-tag destinations"
          description="Pick a tag, then the absolute root its items route to."
          enabled={options.EnableTagDestinations}
          onToggle={(v) => {
            set("EnableTagDestinations", v);
          }}
        >
          <KeyValueMapEditor
            map={options.TagDestinations}
            onChange={(m) => {
              set("TagDestinations", m);
            }}
            renderKey={(draftKey, setDraftKey, existingKeys) => (
              <TagPicker
                label=""
                values={draftKey === "" ? [] : [draftKey]}
                onChange={(values) => {
                  setDraftKey(values.at(-1) ?? "");
                }}
                placeholder="Search tags…"
                excludeValues={existingKeys}
              />
            )}
            renderValue={(value, setValue) => (
              <>
                <TextInput value={value} onChange={setValue} placeholder="Destination root" />
                <PathShapeHint value={value} />
              </>
            )}
            addLabel="Add tag rule"
          />
        </ToggleHeaderCard>

        <ToggleHeaderCard
          title="Advanced routing & safety"
          description="Allowed roots and source-path rules."
          enabled={options.EnableAdvancedRouting}
          onToggle={(v) => {
            set("EnableAdvancedRouting", v);
          }}
        >
          <div>
            <h4 className="text-sm font-semibold text-foreground">Allowed roots</h4>
            <p className="mb-4 mt-1 text-sm text-secondary">
              A rename may only write inside these absolute directories; a target outside them is
              rejected. Empty = files stay within their own source folder.
            </p>
            <TagListInput
              values={options.AllowedRoots}
              onChange={(v) => {
                set("AllowedRoots", v);
              }}
              placeholder="Add an absolute directory, press Enter"
            />
          </div>

          <div>
            <h4 className="text-sm font-semibold text-foreground">Source-path destinations</h4>
            <p className="mb-4 mt-1 text-sm text-secondary">
              Match an item&apos;s source path to a destination root, top rule first. An exact match
              or a regex.
            </p>
            <ObjectArrayEditor<PathDestinationRule>
              rows={options.PathDestinations}
              onChange={(rows) => {
                set("PathDestinations", rows);
              }}
              makeRow={() => ({ Pattern: "", Dest: "", IsRegex: false })}
              renderRow={(row, _i, update) => (
                <>
                  <Field label="Source path">
                    <TextInput
                      value={row.Pattern}
                      onChange={(v) => {
                        update({ Pattern: v });
                      }}
                      mono
                      placeholder="Exact path or regex"
                    />
                  </Field>
                  <Toggle
                    label="Match as a regex"
                    checked={row.IsRegex}
                    onChange={(v) => {
                      update({ IsRegex: v });
                    }}
                  />
                  <RegexValidity pattern={row.Pattern} isRegex={row.IsRegex} />
                  <Field label="Destination root">
                    <TextInput
                      value={row.Dest}
                      onChange={(v) => {
                        update({ Dest: v });
                      }}
                      placeholder="Destination root"
                    />
                    <PathShapeHint value={row.Dest} />
                  </Field>
                </>
              )}
              addLabel="Add path rule"
              ordered
            />
          </div>
        </ToggleHeaderCard>

        <GroupCard
          title="Sidecar files"
          description="Files sharing the primary's basename with one of these extensions move and rename with it; a target that already exists is left untouched, never overwritten. Captions Cove tracks always move regardless."
        >
          <Field label="Also move sidecar files with these extensions">
            <TagListInput
              values={options.AssociatedExtensions}
              onChange={(v) => {
                set("AssociatedExtensions", v);
              }}
              placeholder="Add an extension, press Enter"
              normalize={normalizeSidecarExtension}
              onReject={(candidate) => !/^[a-z0-9]+$/.test(candidate)}
              onLiveChange={(raw) => {
                setSidecarLiveInput(raw);
              }}
            />
            {(() => {
              const advisory = extensionShapeAdvisory(normalizeSidecarExtension(sidecarLiveInput));
              return advisory ? <StatusText kind="warning">{advisory}</StatusText> : null;
            })()}
          </Field>
        </GroupCard>

        <div className="rounded-xl border border-border bg-card p-4">
          <Toggle
            label="Delete the source folder when a move leaves it empty"
            checked={options.RemoveEmptyFolder}
            onChange={(v) => {
              set("RemoveEmptyFolder", v);
            }}
            helper="Deletes a source folder only when a move empties it completely — never a non-empty folder or a root. Undo won't move the file back into a deleted folder; the file stays at its new location. Off by default."
          />
        </div>
      </div>

      <SectionGroupHeader title="Advanced" hint="Power-user controls — collapsed by default" />
      <div className="space-y-3">
        <CollapsibleSection
          title="Clean up the name"
          summary="Illegal-character and space handling, case, ASCII"
        >
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <Field label="Illegal-char replacement">
              <SegmentedReplace
                value={options.IllegalReplacement}
                onChange={(v) => {
                  set("IllegalReplacement", v);
                }}
                stripLabel="Strip"
                replaceLabel="Replace with"
                stripHelper="Illegal characters are removed."
                replaceHelper="Each illegal character becomes this."
                inputPlaceholder="e.g. _"
              />
            </Field>
            <Field label="Space replacement">
              <SegmentedReplace
                value={options.SpaceReplacement}
                onChange={(v) => {
                  set("SpaceReplacement", v);
                }}
                stripLabel="Keep spaces"
                replaceLabel="Replace with"
                stripHelper="Spaces are left as-is."
                replaceHelper="Each space becomes this."
                inputPlaceholder="e.g. _ or ."
              />
            </Field>
            <Field
              label="Remove characters"
              helper="Characters to delete from the name, e.g. ,# — separate from illegal-character handling."
            >
              <TextInput
                value={options.RemoveCharacters}
                onChange={(v) => {
                  set("RemoveCharacters", v);
                }}
                placeholder="e.g. ,#"
              />
            </Field>
            <Field label="Case">
              <Select
                value={options.Case}
                onChange={(v) => {
                  set("Case", v);
                }}
                options={CASE_OPTIONS}
              />
            </Field>
          </div>
          <Toggle
            label="ASCII transliterate"
            checked={options.AsciiTransliterate}
            onChange={(v) => {
              set("AsciiTransliterate", v);
            }}
            helper="Convert accented characters to plain ASCII."
          />
          <Toggle
            label="Normalize punctuation to ASCII"
            checked={options.NormalizePunctuation}
            onChange={(v) => {
              set("NormalizePunctuation", v);
            }}
            helper="Fold curly quotes, en/em dashes, and ellipses to plain ASCII."
          />
        </CollapsibleSection>

        <CollapsibleSection
          title="Length & collisions"
          summary="Length caps, what to drop when too long, duplicate suffix"
        >
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <Field label="Filename max length">
              <NumberInput
                value={options.FilenameMax}
                min={1}
                onChange={(v) => {
                  set("FilenameMax", v);
                }}
              />
            </Field>
            <Field label="Full-path max length">
              <NumberInput
                value={options.FullPathMax}
                min={1}
                onChange={(v) => {
                  set("FullPathMax", v);
                }}
              />
            </Field>
          </div>
          <Field label="Drop order" helper="Fields dropped (top first) when the name is too long.">
            <TagListInput
              values={options.DropOrder}
              onChange={(v) => {
                set("DropOrder", v);
              }}
              ordered
              placeholder="Add field, press Enter"
            />
            <TokenPicker
              tokens={BARE_TOKENS}
              values={options.DropOrder}
              onAdd={(name) => {
                set(
                  "DropOrder",
                  options.DropOrder.includes(name)
                    ? options.DropOrder
                    : [...options.DropOrder, name],
                );
              }}
            />
            <TokenAdvisory values={options.DropOrder} />
          </Field>
          <Field
            label="Duplicate suffix format"
            helper="{n} = a counter added only when a name already exists, e.g. name (1).mp4."
          >
            <ExampleSelect
              value={options.DuplicateSuffixFormat}
              onChange={(v) => {
                set("DuplicateSuffixFormat", v);
              }}
              options={SUFFIX_FORMAT_OPTIONS}
              customPlaceholder=" ({n})"
            />
          </Field>
        </CollapsibleSection>

        <CollapsibleSection
          title="Cross-drive concurrency"
          summary="How many transfers and renames run at once"
        >
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <Field
              label="Cross-volume concurrency"
              helper="How many files to copy across drives at once. Leave at 2 for regular hard drives; raise to 4–8 if both drives are SSDs. Higher is not always faster — on spinning disks it can be slower."
            >
              <NumberInput
                value={options.CrossVolumeConcurrency}
                min={1}
                max={16}
                onChange={(v) => {
                  set("CrossVolumeConcurrency", v);
                }}
              />
            </Field>
            <Field
              label="Same-volume concurrency"
              helper="How many same-drive renames to run at once (these are instant; the default is fine)."
            >
              <NumberInput
                value={options.SameVolumeConcurrency}
                min={1}
                max={16}
                onChange={(v) => {
                  set("SameVolumeConcurrency", v);
                }}
              />
            </Field>
          </div>
        </CollapsibleSection>

        {/* Excludes — the pre-routing skip list, sibling to routing so the two stay parallel.
              These are evaluated before any routing rule; a matching item is dropped from the batch
              entirely (neither renamed nor moved), so they are the safest way to fence off items you
              never want this extension to touch. All three flow through set() like every other control. */}
        <CollapsibleSection
          title="Excludes"
          summary="Skip items by tag, studio, or source path — evaluated before any routing"
        >
          <GroupCard
            title="Exclude by tag"
            description="An item carrying any of these tags is skipped — never renamed, never moved. Evaluated before any routing rule."
          >
            <TagPicker
              label="Tags"
              values={options.ExcludeTags}
              onChange={(v) => {
                set("ExcludeTags", v);
              }}
              placeholder="Search tags…"
            />
          </GroupCard>

          <GroupCard
            title="Exclude by studio"
            description="An item under any of these studios — or under a child of one — is skipped entirely. Evaluated before any routing rule."
          >
            <StudioPicker
              label="Studios"
              values={options.ExcludeStudioIds}
              onChange={(v) => {
                set("ExcludeStudioIds", v);
              }}
              placeholder="Search studios…"
            />
          </GroupCard>

          <GroupCard
            title="Exclude by source path"
            description="An item whose source path matches a rule is skipped entirely. Evaluated before any routing rule. An exact match or a regex."
          >
            <ObjectArrayEditor<ExcludeRule>
              rows={options.ExcludePaths}
              onChange={(rows) => {
                set("ExcludePaths", rows);
              }}
              makeRow={() => ({ Pattern: "", IsRegex: false })}
              renderRow={(row, _i, update) => (
                <>
                  <Field label="Source path">
                    <TextInput
                      value={row.Pattern}
                      onChange={(v) => {
                        update({ Pattern: v });
                      }}
                      mono
                      placeholder="Exact path or regex"
                    />
                  </Field>
                  <Toggle
                    label="Match as a regex"
                    checked={row.IsRegex}
                    onChange={(v) => {
                      update({ IsRegex: v });
                    }}
                  />
                  <RegexValidity pattern={row.Pattern} isRegex={row.IsRegex} />
                </>
              )}
              addLabel="Add exclude rule"
            />
          </GroupCard>
        </CollapsibleSection>

        {/* Field rewriting — shapes a token's value BEFORE the template renders (mirroring the
              ordering note on "Destination routing"/"Excludes"): literal per-token replaces, leading-
              article stripping, the name-shaping toggles, and the per-token whitespace map. All flow
              through set() like every other control. */}
        <CollapsibleSection
          title="Field rewriting & name shaping"
          summary="Literal token replacements, article stripping, and name shaping"
        >
          <GroupCard
            title="Per-token replacements"
            description="A literal find/replace on a single token's value, before the name is shaped. The target is a canonical token name (e.g. studio, title), matched case-insensitively."
          >
            <ObjectArrayEditor<FieldReplaceRule>
              rows={options.FieldReplacers}
              onChange={(rows) => {
                set("FieldReplacers", rows);
              }}
              makeRow={() => ({ TargetToken: TOKEN_OPTIONS[0].value, Find: "", Replace: "" })}
              renderRow={(row, _i, update) => {
                // A rule saved before this dropdown existed (or via a hand-edited blob) may hold a
                // token outside the 18 — surface it as an extra option so the Select shows the real
                // stored value instead of silently displaying the first option while state differs.
                const tokenOptions = TOKEN_OPTIONS.some((o) => o.value === row.TargetToken)
                  ? TOKEN_OPTIONS
                  : [
                      ...TOKEN_OPTIONS,
                      { value: row.TargetToken, label: `${row.TargetToken} (unknown)` },
                    ];
                return (
                  <>
                    <Field label="Target token">
                      <Select
                        value={row.TargetToken}
                        onChange={(v) => {
                          update({ TargetToken: v });
                        }}
                        options={tokenOptions}
                      />
                    </Field>
                    <Field label="Find" helper="Literal text to match. Empty does nothing.">
                      <TextInput
                        value={row.Find}
                        onChange={(v) => {
                          update({ Find: v });
                        }}
                        placeholder="Text to find"
                      />
                    </Field>
                    <Field label="Replace with">
                      <TextInput
                        value={row.Replace}
                        onChange={(v) => {
                          update({ Replace: v });
                        }}
                        placeholder="Replacement (blank to remove)"
                      />
                    </Field>
                  </>
                );
              }}
              addLabel="Add replacement"
            />
          </GroupCard>

          <GroupCard title="Strip leading article">
            <Toggle
              label="Strip a leading article from the title"
              checked={options.StripLeadingArticles}
              onChange={(v) => {
                set("StripLeadingArticles", v);
              }}
              helper="Removes a single leading article and the whitespace after it from the title, at most once (case-insensitive) — a word merely starting with an article, and a mid-title article, are left alone."
            />
            <Field label="Articles">
              <TagListInput
                values={options.Articles}
                onChange={(v) => {
                  set("Articles", v);
                }}
                placeholder="Add article, press Enter"
              />
            </Field>
          </GroupCard>

          <Toggle
            label="Squeeze studio names"
            checked={options.SqueezeStudioNames}
            onChange={(v) => {
              set("SqueezeStudioNames", v);
            }}
            helper="Removes all spaces from the studio value so one studio renders to one stable folder name."
          />
          <Toggle
            label="Drop a performer already in the title"
            checked={options.PreventTitlePerformer}
            onChange={(v) => {
              set("PreventTitlePerformer", v);
            }}
            helper="Drops a performer whose name already appears as a whole word in the title."
          />
          <Toggle
            label="Collapse repeated folder segments"
            checked={options.PreventConsecutiveSegments}
            onChange={(v) => {
              set("PreventConsecutiveSegments", v);
            }}
            helper="Collapses consecutive duplicate folder path segments to one — affects the folder path, not the filename."
          />
        </CollapsibleSection>
      </div>

      {/* ── UNDO — the action surface, distinct from configuration, at the bottom. ── */}
      <div id="rename-undo-section">
        <UndoSection refreshKey={0} />
      </div>

      <SaveBar
        dirty={dirty}
        saving={saving}
        saveError={saveError}
        savedFlash={savedFlash}
        canSave={canSave}
        onSave={() => void onSave()}
        onDiscard={() => {
          setOptions(saved);
        }}
      />
    </div>
  );
}

/**
 * RenameSettingsPanel — the Extensions-tab settings-section entry point. Thin wrapper over the shared
 * {@link RenamePanelBody}. Kept as a distinct export so the host resolves the same `RenameSettingsPanel`
 * componentName with byte-identical behavior; the dedicated page uses the same body via RenamePage.
 */
export function RenameSettingsPanel() {
  return <RenamePanelBody />;
}
