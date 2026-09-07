/**
 * MissingSceneCard — a hand-rolled Cove-styled video card for one missing scene. Cove's frontend SDK exports no
 * card component and there is no build path to the host's `../cove/ui`, so the card piggybacks the host's GLOBAL
 * card CSS classes (`video-card`/`entity-card`/`card-media`/`card-body`/`card-title`, shipped unscoped in the
 * host stylesheet the extension DOM inherits) for the theme, and supplies its base layout with host-token
 * Tailwind utilities. Every explicit dimension rides an element-scoped inline `style` — the host Tailwind JIT
 * never scans this bundle, so an arbitrary-value class (`text-[10px]`) would render nothing (and fails
 * `check-classes`).
 *
 * It mirrors the native videos card's field set for consistency: a 16:9 cover, a two-line title, a date/studio
 * meta row, inline performer chips (avatar + name, +N cap), a two-line description, and a footer with performer
 * and tag counts — plus the always-on four-state Whisparr status glyph and the Monitor action. The fourth state
 * is an abstention: a status the connected Whisparr cannot report draws the indeterminate glyph with its cause in
 * the glyph's title, which is what distinguishes it from the confident "Not added" it replaces. The cover and
 * performer avatars are external metadata-host urls rendered through `<img src>` only; every name/title/overview
 * is a React text node — never raw-HTML injection. Each optional row (chips, description, counts) is omitted when
 * empty (a source that omits performers/tags/overview), never an empty strip. The status pill
 * renders unconditionally — the Missing tab is a Whisparr surface, so status is always on (unlike the native
 * videos page, which hides it behind a toggle); the glyph reuses the shared videos-page SceneStateGlyph so a
 * "Wanted" scene reads with the monitored glyph, not a differently-styled pill. The action row pairs Search (the
 * sole immediate grab) with Monitor (mark wanted) OR — for a scene already on the wanted list — Unmonitor
 * (un-mark); all three are per-scene v3-only, disabled on v2 with the shipped capability copy. A Search that
 * reported nothing to search for states its cause as a visible line beneath the action row, never hover-only, and
 * a click the server refused states its own reason in the same place.
 *
 * An unmet required setting is a property of the CONNECTION, so the card carries only the short requirement on
 * the control it dims; the full consequence sentence is stated once for the whole list by the selection bar. Per
 * card it would render once per row, which on a full grid is the same paragraph forty times.
 */
import { Film, Loader, Minus, Plus, Search, Tag, User } from "lucide-react";
import { StatusText } from "@cove-extensions/ui-shared";
import type { MissingPerformer, MissingSceneStatus } from "../contracts";
import { SCENE_STATE_VISUAL, UNKNOWN_STATE_VISUAL } from "../common/lib/sceneStateVisual";
import { SceneStateGlyph } from "../common/ui/SceneStateGlyph";
import { MISSING_STATUS_LABEL, missingStatusVisualKey, type MissingRow } from "./missingLogic";
import { configShortReason } from "../common/lib/configGuardLogic";
import { useConfigHealth } from "../common/lib/configHealthStore";
import { appendReason, guardedControl } from "../common/lib/refusalAffordanceLogic";

// The landscape (16:9) media box: the cover art the card leads with. The aspect ratio rides an inline style so
// no arbitrary-value Tailwind is needed (the host JIT would not emit it).
const MEDIA_STYLE: React.CSSProperties = { aspectRatio: "16 / 9" };

// The host JIT never emits these bracketed sizes, so each explicit dimension is an element-scoped inline style.
const META_STYLE: React.CSSProperties = { fontSize: "11px" };
const CHIP_NAME_STYLE: React.CSSProperties = { fontSize: "10px", maxWidth: "80px" };
const SMALL_STYLE: React.CSSProperties = { fontSize: "10px" };

// Performer chips cap at 4 (mirrors the native card) before a "+N" overflow.
const PERFORMER_CAP = 4;

/**
 * The card's hover-reveal selection toggle. Its `group-hover:opacity-100` only fires because the card root
 * carries the Tailwind `group` class; the click/pointer handlers preempt the card's own click so selecting a
 * card never triggers its navigation.
 */
function SelectionToggle({
  title,
  selected,
  selecting,
  onToggle,
}: {
  title: string;
  selected: boolean;
  selecting: boolean;
  onToggle: () => void;
}) {
  return (
    <button
      type="button"
      aria-label={selected ? `Deselect ${title}` : `Select ${title}`}
      aria-pressed={selected}
      onClick={(event) => {
        event.preventDefault();
        event.stopPropagation();
        onToggle();
      }}
      onPointerDown={(event) => {
        event.stopPropagation();
      }}
      className={`absolute left-0.5 top-0.5 z-10 flex h-8 w-8 items-center justify-center rounded-md transition-opacity ${
        selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"
      }`}
    >
      <span
        className={`flex h-4 w-4 items-center justify-center rounded border shadow-sm ${
          selected
            ? "border-accent bg-accent text-white"
            : "border-border bg-background/95 text-transparent"
        }`}
      >
        <svg
          viewBox="0 0 16 16"
          className="h-3 w-3"
          fill="none"
          stroke="currentColor"
          strokeWidth="2.2"
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden="true"
        >
          <path d="M3.5 8.25 6.5 11.25 12.5 4.75" />
        </svg>
      </span>
    </button>
  );
}

/**
 * The leading cover: an <img> when the scene carries one, else a muted placeholder tile.
 *
 * ONE candidate, matching Cove's own video card, which renders a single `<img src={coverUrl}>` with no fallback
 * chain. Each source picks the field that actually loads upstream (StashDB its first scene image, ThePornDB its
 * poster). The tile reuses Cove's `Film` placeholder glyph.
 */
function CoverBox({ url }: { url: string | null }) {
  if (url !== null) {
    return <img src={url} alt="" loading="lazy" className="h-full w-full object-cover" />;
  }

  return (
    <div className="flex h-full w-full items-center justify-center bg-card">
      <Film className="h-6 w-6 text-muted" aria-hidden />
    </div>
  );
}

/** One performer chip: a circular avatar (or a User glyph when the source carries no image) + a truncated name. */
function PerformerChip({ performer }: { performer: MissingPerformer }) {
  return (
    <span className="performer-badge inline-flex min-w-0 items-center gap-1 rounded-full border border-border bg-surface px-1.5 py-0.5">
      {performer.imageUrl ? (
        <img
          src={performer.imageUrl}
          alt=""
          loading="lazy"
          className="h-4 w-4 shrink-0 rounded-full object-contain"
        />
      ) : (
        <User className="h-3.5 w-3.5 shrink-0 text-muted" aria-hidden />
      )}
      <span className="truncate text-secondary" style={CHIP_NAME_STYLE}>
        {performer.name}
      </span>
    </span>
  );
}

/** The inline performer chip strip (first 4 + a "+N" overflow). Rendered only when the list is non-empty. */
function PerformerStrip({ performers }: { performers: MissingPerformer[] }) {
  const shown = performers.slice(0, PERFORMER_CAP);
  const overflow = performers.length - shown.length;
  return (
    <div className="flex flex-wrap items-center gap-1.5 overflow-hidden">
      {shown.map((performer, i) => (
        <PerformerChip key={`${performer.name}-${i.toString()}`} performer={performer} />
      ))}
      {overflow > 0 && (
        <span className="shrink-0 text-muted" style={SMALL_STYLE}>
          +{overflow}
        </span>
      )}
    </div>
  );
}

/** The footer count row (👤 performers · 🏷️ tags), mirroring the native card's footer icons. Each shown when > 0. */
function CountsFooter({ performerCount, tagCount }: { performerCount: number; tagCount: number }) {
  return (
    <div className="flex items-center gap-3 text-muted" style={SMALL_STYLE}>
      {performerCount > 0 && (
        <span className="inline-flex items-center gap-1">
          <User className="h-3 w-3" aria-hidden />
          {performerCount}
        </span>
      )}
      {tagCount > 0 && (
        <span className="inline-flex items-center gap-1">
          <Tag className="h-3 w-3" aria-hidden />
          {tagCount}
        </span>
      )}
    </div>
  );
}

/**
 * The always-on Whisparr status (Not added / Wanted / Unmonitored / Status unknown) as a plain inline glyph +
 * label in host tokens — the SAME representation the native videos-page card badge uses (via the shared
 * SCENE_STATE_VISUAL + SceneStateGlyph), so "Wanted" reads as the monitored glyph, not a differently-styled pill.
 * The abstaining key is resolved HERE, in the view, to the standalone indeterminate descriptor: the enum-keyed
 * record is keyed on a pinned wire enum, and the pure module never indexes it. `reason` rides the element's
 * `title` (an attribute value, never markup), which puts the cause at the glyph the user is already looking at.
 */
function MissingStatus({ status, reason }: { status: MissingSceneStatus; reason: string | null }) {
  const visualKey = missingStatusVisualKey(status);
  const visual = visualKey === "unknown" ? UNKNOWN_STATE_VISUAL : SCENE_STATE_VISUAL[visualKey];
  return (
    <span
      className="inline-flex items-center gap-1 text-xs text-secondary"
      title={reason ?? undefined}
    >
      <SceneStateGlyph iconKey={visual.iconKey} color={visual.color} filled={visual.filled} />
      {MISSING_STATUS_LABEL[status]}
    </span>
  );
}

// The shared host-token treatment for the card's small action buttons (Monitor/Unmonitor), matching the Refresh
// button: a bordered pill that accents on hover and dims when disabled.
const ACTION_BUTTON_CLASS =
  "inline-flex shrink-0 items-center gap-1.5 rounded-md border border-border px-2.5 py-1 text-xs font-medium text-secondary transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-60";

/**
 * The per-card Monitor affordance — the active "Mark wanted" button shown when the scene is NOT yet on the wanted
 * list (a wanted card shows Unmonitor instead). Marking wanted adds to Whisparr's wanted list with no immediate
 * grab. A version that can't offer per-scene Monitor (v2) is disabled with the shipped capability copy.
 */
function MonitorButton({
  versionSupported,
  versionDisabledTitle,
  configRequirement,
  onMonitor,
}: {
  versionSupported: boolean;
  versionDisabledTitle: string;
  // The SHORT reason naming the setting an unusable stored configuration is missing, threaded from the card
  // exactly as versionDisabledTitle is — this sub-component stays presentational. The full consequence sentence
  // is a property of the connection and is stated once for the whole list, in the selection bar.
  configRequirement: string | null;
  onMonitor: () => void;
}) {
  const affordance = guardedControl({
    name: "Mark this scene wanted",
    enabledTitle: "Mark wanted — add to Whisparr's wanted list (no immediate grab)",
    capabilityReason: versionSupported ? null : versionDisabledTitle,
    configurationReason: configRequirement,
  });
  return (
    <button
      type="button"
      onClick={onMonitor}
      disabled={affordance.disabled}
      aria-label={affordance.ariaLabel}
      title={affordance.title}
      className={ACTION_BUTTON_CLASS}
    >
      <Plus className="h-3.5 w-3.5" />
      Monitor
    </button>
  );
}

/**
 * The per-card Unmonitor affordance — shown only for a scene currently on the wanted list. Un-marking removes it
 * from the wanted list (monitored:false) with no grab. Per-scene monitor is v3-only, so on v2 it is disabled with
 * the shipped capability copy.
 */
function UnmonitorButton({
  ariaLabel,
  versionSupported,
  versionDisabledTitle,
  onUnmonitor,
}: {
  ariaLabel: string;
  versionSupported: boolean;
  versionDisabledTitle: string;
  onUnmonitor: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onUnmonitor}
      disabled={!versionSupported}
      aria-label={ariaLabel}
      title={
        versionSupported
          ? "Unmonitor — remove from Whisparr's wanted list (no grab)"
          : versionDisabledTitle
      }
      className={ACTION_BUTTON_CLASS}
    >
      <Minus className="h-3.5 w-3.5" />
      Unmonitor
    </button>
  );
}

/**
 * The per-card Search affordance — the ONLY per-card control that issues an immediate grab ("get it now"). A
 * per-scene grab needs a scene-level Whisparr row to command, which only v3 has, so on v2 it is disabled with the
 * shipped capability copy (the monitored-library search verb stays available on either generation). It shows an
 * in-flight spinner while the grab command posts; a grab changes no immediate row state, so the button carries no
 * confirmed/disabled after-state beyond the transient spinner. A click that reported nothing to search for carries
 * its cause in both attributes here AND as the card's visible line beneath the action row.
 */
function SearchButton({
  ariaLabel,
  searching,
  refusalReason,
  versionSupported,
  versionDisabledTitle,
  onSearch,
}: {
  ariaLabel: string;
  searching: boolean;
  refusalReason: string | null;
  versionSupported: boolean;
  versionDisabledTitle: string;
  onSearch: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onSearch}
      disabled={searching || !versionSupported}
      aria-label={appendReason(ariaLabel, refusalReason)}
      // The two causes cannot collide: the refusal derivation yields nothing on a generation that cannot search
      // per-scene, so the capability copy always wins wherever it applies.
      title={
        versionSupported
          ? (refusalReason ?? "Search now — grab this scene now")
          : versionDisabledTitle
      }
      className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md border border-border text-secondary transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-60"
    >
      {searching ? (
        <Loader className="h-3.5 w-3.5 animate-spin" />
      ) : (
        <Search className="h-3.5 w-3.5" />
      )}
    </button>
  );
}

/**
 * One missing-scene card, mirroring the native Cove VideoCard field set: a 16:9 cover over a body holding a
 * two-line title, a two-span date·studio meta row, inline performer chips (avatar + name), a two-line
 * description, a performer/tag counts footer, and an action row pairing the always-on status pill with the
 * Monitor cluster. Owned-only affordances (preview, scrub, rating, bookmark, quick-view, the /video link, the
 * resolution/duration specs overlay) are omitted — a non-owned missing scene has no file or state for them.
 */
export function MissingSceneCard({
  row,
  versionSupported,
  versionDisabledTitle,
  statusReason = null,
  searchReason = null,
  refusalReason = null,
  selected,
  selecting,
  searching,
  onToggleSelect,
  onMonitor,
  onUnmonitor,
  onSearch,
}: {
  row: MissingRow;
  versionSupported: boolean;
  versionDisabledTitle: string;
  // The sentence naming why an abstaining status cannot be known, derived ONCE per response by the tab (the
  // cause is a property of the connected generation and the current read, not of the row) and null for a status
  // the server asserted.
  statusReason?: string | null;
  // The sentence naming why a Search click did not search, derived by the tab from the outcome the response
  // returned. Null until this row has been clicked, and null while the status abstains — an outage is drawn once
  // over the set, so a card in that state must not also blame itself.
  searchReason?: string | null;
  // The composed line for a click this card's own action row issued and the server refused, keyed by row in the
  // tab. Null until this row's last click failed; it and the pre-click configuration reason can both be non-null
  // only where a settings change landed between the click and the render, and both are true statements then.
  refusalReason?: string | null;
  selected: boolean;
  // Whether any card in the list is selected — keeps every card's toggle solid (not hover-hidden) once a
  // selection is in progress, mirroring the native card.
  selecting: boolean;
  searching: boolean;
  onToggleSelect: (sourceId: string) => void;
  onMonitor: (sourceId: string) => void;
  onUnmonitor: (sourceId: string) => void;
  onSearch: (sourceId: string) => void;
}) {
  const config = useConfigHealth();
  // Gated on !loading so an in-flight read never dims a card, and a failed read reports nothing unmet.
  const configRequirement =
    !config.loading && config.missingRequiredOptions.length > 0
      ? configShortReason(config.missingRequiredOptions)
      : null;
  const cover = row.coverUrl ?? row.posterUrl;
  const hasCounts = row.performers.length > 0 || row.tags.length > 0;
  // On the wanted list either by the server's own status or this session's optimistic Monitor — a wanted card
  // offers Unmonitor (un-mark), a non-wanted one offers Monitor (mark wanted). Search is offered in both states.
  const isWanted = row.wanted || row.status === "wanted";

  return (
    <div
      className={`video-card group relative flex h-full flex-col overflow-hidden rounded border bg-card ${
        selected ? "border-accent" : "border-border"
      }`}
    >
      <div className="card-media relative w-full overflow-hidden" style={MEDIA_STYLE}>
        <CoverBox url={cover} />
        <SelectionToggle
          title={row.title}
          selected={selected}
          selecting={selecting}
          onToggle={() => {
            onToggleSelect(row.sourceId);
          }}
        />
      </div>
      <div className="card-body flex min-w-0 flex-1 flex-col gap-1.5 border-t border-border/50 px-2.5 pb-2 pt-2">
        <div>
          <span
            className="card-title block font-semibold leading-snug text-foreground line-clamp-2"
            title={row.title}
          >
            {row.title}
          </span>
          {(row.releaseDate !== null || row.studioName !== null) && (
            <div className="mt-1 flex items-center gap-2 text-muted" style={META_STYLE}>
              {row.releaseDate !== null && <span>{row.releaseDate}</span>}
              {row.studioName !== null && <span className="truncate">{row.studioName}</span>}
            </div>
          )}
        </div>
        {row.performers.length > 0 && <PerformerStrip performers={row.performers} />}
        {row.overview !== null && (
          <p className="text-secondary line-clamp-2" style={SMALL_STYLE}>
            {row.overview}
          </p>
        )}
        {hasCounts && (
          <CountsFooter performerCount={row.performers.length} tagCount={row.tags.length} />
        )}
        <div className="mt-auto flex items-center justify-between gap-2 pt-1">
          <MissingStatus status={row.status} reason={statusReason} />
          <div className="flex shrink-0 items-center gap-1.5">
            <SearchButton
              ariaLabel={`Search for ${row.title} now`}
              searching={searching}
              refusalReason={searchReason}
              versionSupported={versionSupported}
              versionDisabledTitle={versionDisabledTitle}
              onSearch={() => {
                onSearch(row.sourceId);
              }}
            />
            {isWanted ? (
              <UnmonitorButton
                ariaLabel={`Remove ${row.title} from the wanted list`}
                versionSupported={versionSupported}
                versionDisabledTitle={versionDisabledTitle}
                onUnmonitor={() => {
                  onUnmonitor(row.sourceId);
                }}
              />
            ) : (
              <MonitorButton
                versionSupported={versionSupported}
                versionDisabledTitle={versionDisabledTitle}
                configRequirement={configRequirement}
                onMonitor={() => {
                  onMonitor(row.sourceId);
                }}
              />
            )}
          </div>
        </div>
        {refusalReason !== null && (
          <div role="status">
            <StatusText kind="warning">{refusalReason}</StatusText>
          </div>
        )}
        {searchReason !== null && (
          <div role="status">
            <StatusText kind="warning">{searchReason}</StatusText>
          </div>
        )}
      </div>
    </div>
  );
}
