/**
 * WhisparrScenePanel — the native per-scene surface: the "Whisparr" tab in the video detail rail. It
 * is a TAB, so it receives `{ entityId }` (the Cove video id) as EntityTabProps — NOT the spread slot context
 * the action-row/toolbar slots get. It posts `{ CoveId: entityId }` to `/scene-detail` through the shared
 * {@link ./sceneStatusStore} (one fetch per scene open) and renders, in a fixed priority order:
 *   1. the status badge header (glyph + label — never color-only);
 *   2. an "Add to Whisparr" affordance shown only when the scene is not yet added (POST /scene-add);
 *   3. a LIVE "Monitor this scene" toggle (POST /scene-monitor), pressed when the scene is monitored;
 *   4. a "Grab quality upgrades" toggle (POST /scene-search-upgrades) — Whisparr v3 has no scene field
 *      distinct from `monitored` for upgrade-monitoring, so the toggle reflects `monitored` and its action
 *      ensures-monitored-then-searches-for-upgrades (documented fallback);
 *   5. the Whisparr-only metadata rows (Quality · Cutoff) — mono micro-labels, each omitted when absent;
 *   6. an "Interactive search" that lazily fetches the pickable release list on first expand (never eager),
 *      each row grabbing its release (POST /scene-grab-release), plus a plain "Search for this scene" (auto);
 *   7. an "Exclude from Whisparr" control reading "Remove exclusion" when the scene is already
 *      excluded, behind a quiet window.confirm gate (the host exposes no dialog API to a tab).
 *
 * Each mutation refreshes the shared scene detail (refreshSceneDetail) so the badge + controls update without a
 * duplicate fetch; a failed mutation leaves the prior state intact. It restates NO Cove-owned field (no release
 * date, file size, runtime, resolution — LOCKED): only Whisparr-owned facts. Whisparr's Profile + Last-grabbed
 * are not carried on the /scene-detail wire, so those design rows degrade out honestly rather than showing a
 * guessed value. Styling uses host Tailwind token classes only (check-classes); all text is React nodes.
 */
import { useState } from "react";
import {
  Ban,
  Bookmark,
  Circle,
  CircleDashed,
  Download,
  Loader,
  Plus,
  RotateCcw,
  Search,
  SlidersHorizontal,
  TrendingUp,
} from "lucide-react";
import { request, ApiError } from "@cove/extension-sdk";
import { StatusText } from "@cove-ext/ui-shared";
import type { EntityTabProps } from "@cove/extension-sdk";
import { VERSION_CAPABILITY_COPY, WHISPARR_UNAVAILABLE_COPY } from "./monitorLogic";
import { missingIdMessage } from "./identityGuardLogic";
import { WhisparrLogo } from "./WhisparrLogo";
import { FILE_INDICATOR, stateBadge } from "./sceneStatusLogic";
import {
  releaseSummary,
  sceneAddBody,
  sceneControlState,
  sceneExclusionBody,
  sceneGrabReleaseBody,
  sceneMonitorBody,
  sceneSearchBody,
  type ReleaseRow,
} from "./sceneActionsLogic";
import { actionFailureCopy } from "./actionFailureLogic";
import {
  EXTENSION_ID,
  fetchReleaseList,
  refreshSceneDetail,
  useSceneDetail,
} from "./sceneStatusStore";

/** A quiet single-line message for the non-renderable outcomes (loading / no identity / v2 / error). */
function MessageRow({ text }: { text: string }) {
  return <p className="text-sm text-secondary">{text}</p>;
}

/** One Whisparr-only metadata row: a mono uppercase micro-label + its value. Omitted by the caller when absent. */
function MetaRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-3">
      <span className="text-xs font-medium uppercase tracking-wide text-muted">{label}</span>
      <span className="text-sm text-foreground">{value}</span>
    </div>
  );
}

// The scene-panel header reuses sceneStatusLogic.stateBadge (label + iconKey); this maps the iconKey to the
// same lucide glyphs WhisparrCardBadge uses, so the header, card, and toolbar share one legend.
const STATE_GLYPH: Record<string, { Icon: typeof Bookmark; color: string; fill: boolean }> = {
  bookmark: { Icon: Bookmark, color: "text-accent", fill: true },
  circle: { Icon: Circle, color: "text-secondary", fill: false },
  circleDashed: { Icon: CircleDashed, color: "text-muted", fill: false },
  ban: { Icon: Ban, color: "text-red-400", fill: false },
};

/**
 * The compact management-state header: the Whisparr brand marker + the primary four-state badge (glyph +
 * label, never color-only) + the SECONDARY file dot when the scene has a file. Reuses the shared
 * {@link stateBadge}, so it single-sources the card/toolbar legend rather than reintroducing a widget.
 */
function ScenePanelHeader({
  state,
  hasFile,
}: {
  state: Parameters<typeof stateBadge>[0];
  hasFile: boolean;
}) {
  const badge = stateBadge(state);
  const glyph = STATE_GLYPH[badge.iconKey] ?? STATE_GLYPH.circleDashed;
  const StateIcon = glyph.Icon;
  return (
    <div className="flex items-center justify-between gap-2">
      <div className="flex items-center gap-2">
        <WhisparrLogo className="h-4 w-4" />
        <span className="text-xs font-semibold uppercase tracking-wide text-secondary">
          Whisparr
        </span>
      </div>
      <span className="inline-flex items-center gap-1.5 rounded-md border border-border bg-card px-2 py-0.5 text-xs font-medium text-foreground">
        <StateIcon
          className={`h-4 w-4 ${glyph.color}`}
          fill={glyph.fill ? "currentColor" : "none"}
        />
        {badge.label}
        {hasFile && (
          <Download
            className="ml-0.5 h-3.5 w-3.5 text-green-400"
            aria-label={FILE_INDICATOR.label}
            role="img"
          />
        )}
      </span>
    </div>
  );
}

/** Which scene mutation is in flight (disables the whole control set + shows a spinner on the acting control). */
type Pending = "add" | "monitor" | "upgrades" | "search" | "exclude" | "grab" | null;

/** The verb phrase for each pending tag's failure message ("Couldn't {phrase} — {error}."). */
const ACTION_LABELS: Record<Exclude<Pending, null>, string> = {
  add: "add this scene to Whisparr",
  monitor: "update monitoring for this scene",
  upgrades: "search for quality upgrades",
  search: "search for this scene",
  exclude: "update this scene's exclusion",
  grab: "grab that release",
};

export function WhisparrScenePanel({ entityId }: EntityTabProps) {
  const state = useSceneDetail(entityId);
  const [expanded, setExpanded] = useState(false);
  const [releases, setReleases] = useState<ReleaseRow[] | null>(null);
  const [releasesLoading, setReleasesLoading] = useState(false);
  const [releasesFailed, setReleasesFailed] = useState(false);
  const [pending, setPending] = useState<Pending>(null);
  const [grabbingGuid, setGrabbingGuid] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  if (state.loading) {
    return (
      <div className="flex items-center gap-2 p-4 text-sm text-secondary">
        <Loader className="h-4 w-4 animate-spin" />
        <span>Checking Whisparr…</span>
      </div>
    );
  }

  if (state.noIdentity) {
    // The missing-id guard (§3.6): the triggering action is DISABLED with a version-aware, provider-named
    // reason shown both as its aria-label/tooltip AND as a visible inline line — never a dead button. The
    // wording is single-sourced in identityGuardLogic so it cannot drift from the monitor button's copy (C10).
    const reason = missingIdMessage("scene", state.provider);
    return (
      <div className="flex flex-col gap-3 p-4">
        <button
          type="button"
          disabled
          title={reason}
          aria-label={reason}
          className="inline-flex items-center justify-center gap-2 rounded-lg border border-border px-4 py-2 text-sm font-medium text-foreground disabled:cursor-not-allowed disabled:opacity-60"
        >
          <Plus className="h-4 w-4" />
          Add to Whisparr
        </button>
        <div role="status">
          <StatusText kind="warning">{reason}</StatusText>
        </div>
      </div>
    );
  }
  if (state.unsupported) {
    return (
      <div className="p-4">
        <MessageRow text={VERSION_CAPABILITY_COPY} />
      </div>
    );
  }
  if (state.error || state.detail === null) {
    return (
      <div className="p-4">
        <MessageRow text={WHISPARR_UNAVAILABLE_COPY} />
      </div>
    );
  }

  const detail = state.detail;
  const control = sceneControlState(detail);
  const busy = pending !== null;
  const cutoffText =
    detail.cutoffMet === true ? "Met" : detail.cutoffMet === false ? "Unmet" : null;

  // v2 (Sonarr) has no per-scene operations, and its scenes aren't StashDB-keyed for the status projector.
  if (!detail.actionsSupported) {
    return (
      <div className="p-4">
        <MessageRow text={`Per-scene Whisparr actions — ${VERSION_CAPABILITY_COPY}`} />
      </div>
    );
  }

  // Post one scene mutation, then refresh the shared detail so the badge + controls update. A failure now
  // shows the caught error via `actionError`/`StatusText` instead of leaving the control silently stuck.
  async function mutate(which: Exclude<Pending, null>, path: string, body: unknown) {
    setPending(which);
    setActionError(null);
    try {
      await request(`/extensions/${EXTENSION_ID}/${path}`, {
        method: "POST",
        body: JSON.stringify(body),
      });
      await refreshSceneDetail(entityId);
    } catch (err) {
      const respStatus = err instanceof ApiError ? err.status : -1;
      const respBody = err instanceof ApiError ? err.body : null;
      setActionError(
        actionFailureCopy(ACTION_LABELS[which], respStatus, respBody, VERSION_CAPABILITY_COPY),
      );
    } finally {
      setPending(null);
    }
  }

  // Expand the interactive search, fetching the pickable release list ONLY on the first expand (never eagerly).
  async function toggleReleases() {
    const next = !expanded;
    setExpanded(next);
    if (next && releases === null && !releasesFailed && !releasesLoading) {
      setReleasesLoading(true);
      const rows = await fetchReleaseList(entityId);
      setReleasesLoading(false);
      if (rows === null) setReleasesFailed(true);
      else setReleases(rows);
    }
  }

  // Grab one picked release, then refresh so the badge reflects the new download; a failure now shows the
  // caught error via `actionError`/`StatusText` instead of leaving the row silently stuck.
  async function grab(row: ReleaseRow) {
    if (!row.guid) return;
    setPending("grab");
    setGrabbingGuid(row.guid);
    setActionError(null);
    try {
      await request(`/extensions/${EXTENSION_ID}/scene-grab-release`, {
        method: "POST",
        body: JSON.stringify(sceneGrabReleaseBody(entityId, row.guid, row.indexerId)),
      });
      await refreshSceneDetail(entityId);
    } catch (err) {
      const respStatus = err instanceof ApiError ? err.status : -1;
      const respBody = err instanceof ApiError ? err.body : null;
      setActionError(
        actionFailureCopy(ACTION_LABELS.grab, respStatus, respBody, VERSION_CAPABILITY_COPY),
      );
    } finally {
      setPending(null);
      setGrabbingGuid(null);
    }
  }

  function confirmExclusion() {
    const message = control.excluded
      ? "Remove this scene's Whisparr import-list exclusion? Whisparr will be allowed to add it again."
      : "Exclude this scene from Whisparr? It won't be added by future imports until you remove the exclusion.";
    if (window.confirm(message)) {
      void mutate("exclude", "scene-exclusion", sceneExclusionBody(entityId, !control.excluded));
    }
  }

  const ExcludeIcon = control.excluded ? RotateCcw : Ban;

  return (
    <div className="flex flex-col gap-4 p-4">
      {/* 1. Status header — the brand marker + the primary four-state management badge (glyph + label) plus
          the secondary file dot, single-sourced from sceneStatusLogic.stateBadge. */}
      <ScenePanelHeader state={detail.state} hasFile={detail.hasFile} />

      {actionError !== null && (
        <div role="alert">
          <StatusText kind="error">{actionError}</StatusText>
        </div>
      )}

      {/* 2. Add to Whisparr — only when the scene is not yet added. Adds WITHOUT forcing a search. */}
      {control.showAdd && (
        <button
          type="button"
          onClick={() => {
            void mutate("add", "scene-add", sceneAddBody(entityId));
          }}
          disabled={busy}
          title="Add this scene to Whisparr"
          aria-label="Add this scene to Whisparr"
          className="inline-flex items-center justify-center gap-2 rounded-lg border border-border px-4 py-2 text-sm font-medium text-foreground transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-60"
        >
          {pending === "add" ? (
            <Loader className="h-4 w-4 animate-spin" />
          ) : (
            <Plus className="h-4 w-4" />
          )}
          Add to Whisparr
        </button>
      )}

      {/* 3. Monitor this scene — LIVE. Server does add-then-monitor when the scene is not-added. */}
      <div className="flex items-center justify-between gap-3 rounded-lg border border-border p-3">
        <span className="text-sm font-medium text-foreground">Monitor this scene</span>
        <button
          type="button"
          onClick={() => {
            void mutate(
              "monitor",
              "scene-monitor",
              sceneMonitorBody(entityId, !control.monitorPressed),
            );
          }}
          disabled={busy}
          aria-pressed={control.monitorPressed}
          title={
            control.monitorPressed
              ? "Monitoring in Whisparr — click to stop"
              : "Monitor this scene in Whisparr"
          }
          aria-label={control.monitorPressed ? "Stop monitoring this scene" : "Monitor this scene"}
          className={`inline-flex items-center rounded-md border border-border px-3 py-1 text-xs transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-60 ${
            control.monitorPressed ? "text-accent" : "text-secondary"
          }`}
        >
          {pending === "monitor" ? (
            <Loader className="mr-1 h-3 w-3 animate-spin" />
          ) : (
            <Bookmark
              className="mr-1 h-3 w-3"
              fill={control.monitorPressed ? "currentColor" : "none"}
            />
          )}
          {control.monitorPressed ? "Monitoring" : "Monitor"}
        </button>
      </div>

      {/* 4. Grab quality upgrades. Ensures monitored + searches for an upgrade above the cutoff. */}
      <div className="flex items-center justify-between gap-3 rounded-lg border border-border p-3">
        <div className="flex flex-col">
          <span className="text-sm font-medium text-foreground">Grab quality upgrades</span>
          <span className="text-xs text-secondary">
            Search for a better release above the cutoff.
          </span>
        </div>
        <button
          type="button"
          onClick={() => {
            void mutate("upgrades", "scene-search-upgrades", sceneSearchBody(entityId));
          }}
          disabled={busy || !control.interactiveAvailable}
          aria-pressed={control.upgradesPressed}
          title={
            control.interactiveAvailable
              ? "Search Whisparr for a quality upgrade for this scene"
              : "Add this scene to Whisparr first"
          }
          aria-label="Grab quality upgrades"
          className={`inline-flex items-center rounded-md border border-border px-3 py-1 text-xs transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-60 ${
            control.upgradesPressed ? "text-accent" : "text-secondary"
          }`}
        >
          {pending === "upgrades" ? (
            <Loader className="mr-1 h-3 w-3 animate-spin" />
          ) : (
            <TrendingUp className="mr-1 h-3 w-3" />
          )}
          {control.upgradesPressed ? "On" : "Off"}
        </button>
      </div>

      {/* 5. Whisparr-only metadata rows — mono micro-labels; each omitted when Whisparr reports nothing. */}
      {(detail.quality !== null || cutoffText !== null) && (
        <div className="flex flex-col gap-2 rounded-lg border border-border p-3">
          {detail.quality !== null && <MetaRow label="Quality" value={detail.quality} />}
          {cutoffText !== null && <MetaRow label="Cutoff" value={cutoffText} />}
        </div>
      )}

      {/* 6. Interactive search — the pickable release list, loaded lazily ONLY on expand; each row grabs. */}
      <div className="rounded-lg border border-border">
        <button
          type="button"
          onClick={() => {
            void toggleReleases();
          }}
          disabled={!control.interactiveAvailable}
          aria-expanded={expanded}
          title={
            control.interactiveAvailable
              ? "List indexer releases for this scene and grab one"
              : "Add this scene to Whisparr first"
          }
          className="flex w-full items-center justify-between p-3 text-sm text-foreground transition-colors hover:bg-card disabled:cursor-not-allowed disabled:opacity-60"
        >
          <span className="inline-flex items-center gap-2">
            <SlidersHorizontal className="h-4 w-4 text-secondary" />
            Interactive search
          </span>
          {expanded && releasesLoading ? (
            <Loader className="h-4 w-4 animate-spin text-secondary" />
          ) : (
            <span className="text-secondary">{expanded ? "Hide" : "Show"}</span>
          )}
        </button>
        {expanded && !releasesLoading && (
          <div className="border-t border-border">
            {releasesFailed || releases === null ? (
              <p className="p-3 text-sm text-secondary">Couldn&apos;t load releases.</p>
            ) : releases.length === 0 ? (
              <p className="p-3 text-sm text-secondary">No releases available at indexers.</p>
            ) : (
              <>
                <p className="px-3 pt-3 text-xs text-secondary">
                  {releases.length} releases available at indexers
                </p>
                <ul className="flex flex-col p-2">
                  {releases.map((row, i) => (
                    <li key={row.guid ?? `row-${i}`}>
                      <button
                        type="button"
                        onClick={() => {
                          void grab(row);
                        }}
                        disabled={busy || !row.guid}
                        title="Grab this release"
                        className="flex w-full items-center justify-between gap-3 rounded-md px-2 py-2 text-left transition-colors hover:bg-card disabled:cursor-not-allowed disabled:opacity-60"
                      >
                        <span className="min-w-0 flex-1">
                          {row.title !== null && row.title !== undefined && row.title !== "" && (
                            <span className="block truncate text-sm text-foreground">
                              {row.title}
                            </span>
                          )}
                          <span className="block truncate text-xs text-secondary">
                            {releaseSummary(row)}
                          </span>
                        </span>
                        {pending === "grab" && grabbingGuid === row.guid ? (
                          <Loader className="h-4 w-4 shrink-0 animate-spin text-secondary" />
                        ) : (
                          <Download className="h-4 w-4 shrink-0 text-secondary" />
                        )}
                      </button>
                    </li>
                  ))}
                </ul>
              </>
            )}
          </div>
        )}
      </div>

      {/* 6b. Search for this scene — LIVE (auto). Enabled only once the scene is added to Whisparr. */}
      <button
        type="button"
        onClick={() => {
          void mutate("search", "scene-search", sceneSearchBody(entityId));
        }}
        disabled={busy || !control.searchEnabled}
        title={
          control.searchEnabled
            ? "Search Whisparr for this scene now"
            : "Add this scene to Whisparr first"
        }
        aria-label="Search for this scene"
        className="inline-flex items-center justify-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white disabled:cursor-not-allowed disabled:opacity-60"
      >
        {pending === "search" ? (
          <Loader className="h-4 w-4 animate-spin" />
        ) : (
          <Search className="h-4 w-4" />
        )}
        Search for this scene
      </button>

      {/* 7. Exclude from Whisparr; reads "Remove exclusion" when already excluded. Quiet confirm. */}
      <button
        type="button"
        onClick={confirmExclusion}
        disabled={busy}
        title={
          control.excluded
            ? "Remove this scene's Whisparr exclusion"
            : "Exclude this scene from Whisparr"
        }
        aria-label={control.excludeLabel}
        className="inline-flex items-center justify-center gap-2 rounded-lg border border-border px-4 py-2 text-sm font-medium text-secondary transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-60"
      >
        {pending === "exclude" ? (
          <Loader className="h-4 w-4 animate-spin" />
        ) : (
          <ExcludeIcon className="h-4 w-4" />
        )}
        {control.excludeLabel}
      </button>
    </div>
  );
}
