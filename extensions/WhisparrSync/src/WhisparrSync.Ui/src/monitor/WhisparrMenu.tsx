/**
 * WhisparrMenu — OUR OWN Whisparr popover menu on a studio/performer, opened from the action-row
 * {@link ./WhisparrMonitorButton} logo. Cove's built-in "⋮" Actions menu on studio/performer exposes NO
 * extension hook (only the video page does), so instead of folding into Cove's ⋮ the extension renders this
 * small branded menu from the logo it already owns.
 *
 * It is rendered through a React portal to `document.body` with FIXED positioning computed from the trigger's
 * `getBoundingClientRect()`, so the action-row container's overflow can never clip it (the host renders the
 * component inside the slot, not as a top-level popover, so an overflowing menu would otherwise be clipped).
 * It closes on outside pointer-down and on Escape (returning focus to the trigger, handled by the trigger), and
 * is keyboard-navigable (Arrow up/down across the items, `role="menu"` with `menuitemcheckbox` / `menuitemradio`
 * / `menuitem`).
 *
 * This menu is the one and only home of the entity's Whisparr controls, driven by menuItemsState + the shared
 * {@link ./monitorStatusStore} entry for this entity:
 *   - a brand header (the {@link ./WhisparrLogo} + "Whisparr" — the user rule: every Whisparr menu is branded);
 *   - the Monitor toggle (`menuitemcheckbox`) — POSTs `/monitor` to flip the state;
 *   - the monitor-scope choice (a `menuitemradio` group) — "how" the toggle acquires: new releases only, or the
 *     whole back-catalogue; re-applied immediately when the entity is already monitored;
 *   - the bulk actions, shown only while monitored: "Add all missing" (v3-only) / "Reflect owned in Whisparr"
 *     (both versions) / "Search all monitored" (both) — each gated by the connected version's capability flags.
 *
 * The monitor status LINE is deliberately NOT rendered here: it lives once on the page in the
 * {@link ./WhisparrStatusLine} *-detail-bottom slot, so the menu would only duplicate it.
 *
 * A mutation refreshes the shared monitor state (monitorStatusStore.refresh) so the logo tint, the
 * *-detail-bottom status line, and this menu update in lockstep. Styling uses host Tailwind token classes only
 * (no hex, no CSS bundle — check-classes); the fixed position is set via inline numeric top/left/width; all
 * labels are React nodes (auto-escaped).
 */
import { useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Check, Circle, CircleDot, FileCheck2, Loader, PlusCircle, Search } from "lucide-react";
import { request } from "../common/lib/coveApi";
import { StatusText, useOverlayKeys } from "@cove-extensions/ui-shared";
import { WhisparrLogo } from "../common/ui/WhisparrLogo";
import {
  DEFAULT_MONITOR_SCOPE,
  MONITOR_SCOPE_HEADING,
  MONITOR_SCOPE_HELP,
  MONITOR_SCOPE_OPTIONS,
  VERSION_CAPABILITY_COPY,
  monitorRequestBody,
} from "./monitorLogic";
import type { EntityKind, MonitorScope, RemoteIdPair } from "../contracts";
import {
  bulkAddMissingBody,
  bulkSearchMonitoredBody,
  menuItemsState,
  reflectOwnedBody,
} from "../common/lib/sceneActionsLogic";
import { useWhisparrMutation } from "../common/lib/useWhisparrMutation";
import { configGuardMessage, configShortReason } from "../common/lib/configGuardLogic";
import { useConfigHealth } from "../common/lib/configHealthStore";
import { guardedControl } from "../common/lib/refusalAffordanceLogic";
import { api } from "../common/lib/extension";
import { useMonitorStatus } from "./monitorStatusStore";

interface WhisparrMenuProps {
  /** The entity kind — drives which endpoint kind is sent. */
  kind: EntityKind;
  /** The entity's remote-id pairs (forwarded to /monitor and /bulk-search-monitored; the server resolves the id). */
  remoteIds: RemoteIdPair[];
  /** The Cove entity id (Studio.Id / Performer.Id) — the bulk-add-missing diff key; null disables that item. */
  coveEntityId: number | null;
  /** The trigger button, for fixed-position anchoring + outside-click discrimination. */
  triggerRef: React.RefObject<HTMLButtonElement | null>;
  /** Close the menu (the trigger returns focus to itself). */
  onClose: () => void;
}

/** The fixed menu width (px); the menu is right-aligned to the trigger so it never overflows off-screen right. */
const MENU_WIDTH = 264;

export function WhisparrMenu({
  kind,
  remoteIds,
  coveEntityId,
  triggerRef,
  onClose,
}: WhisparrMenuProps) {
  const { state, refresh } = useMonitorStatus(kind, remoteIds);
  const config = useConfigHealth();
  const menuRef = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);
  const [selectedScope, setSelectedScope] = useState<MonitorScope>(DEFAULT_MONITOR_SCOPE);
  const { busy, error, run } = useWhisparrMutation();

  const menu = menuItemsState(state.status);
  const monitored = menu.monitorChecked;
  const addSupported = state.status?.addSupported ?? false;
  const ownedImportSupported = state.status?.ownedImportSupported ?? false;

  // Gated on !loading so an in-flight read never disables an item, and a failed read reports nothing unmet — the
  // term can only fire on a KNOWN unmet setting. This menu only renders once the capability check has passed
  // (WhisparrMonitorButton withholds it while unsupported), so the capability-over-configuration precedence is
  // structural here rather than another coalescing arm.
  const configIncomplete = !config.loading && config.missingRequiredOptions.length > 0;
  // The full sentence is the menu's one statement; the short requirement is what each dimmed item carries.
  const configSentence = configIncomplete
    ? configGuardMessage(config.missingRequiredOptions)
    : null;
  const configRequirement = configIncomplete
    ? configShortReason(config.missingRequiredOptions)
    : null;

  const monitorAffordance = guardedControl({
    name: menu.monitorLabel,
    configurationReason: configRequirement,
    busy,
  });
  const addAllAffordance = guardedControl({
    name: "Add all missing",
    enabledTitle: "Register every Cove scene not yet in Whisparr (no grab)",
    // An entity with no Cove id cannot have its missing scenes enumerated at all, and no setting changes that,
    // so it outranks the configuration requirement rather than queueing behind it.
    identityReason:
      coveEntityId === null
        ? "This entity has no Cove id, so its missing scenes can't be enumerated."
        : null,
    configurationReason: configRequirement,
    busy,
  });

  // Anchor to the trigger before paint so the popover never flashes at the wrong spot. Right-align to the
  // button (the action row sits top-right) and clamp to the viewport's left edge.
  useLayoutEffect(() => {
    const el = triggerRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const left = Math.max(8, rect.right - MENU_WIDTH);
    setPos({ top: rect.bottom + 6, left });
  }, [triggerRef]);

  // triggerRef is excluded from outside-click so a click on the trigger toggles the menu instead of
  // closing then immediately reopening it.
  useOverlayKeys(menuRef, { nav: "menu", onClose, excludeRefs: [triggerRef] });

  // POST one mutation, then refresh the shared monitor state (logo tint + status line + this menu in lockstep).
  // The menu is kept open through the run: the checkbox/status flip is the visible confirmation.
  async function act(path: string, body: unknown, label: string) {
    await run({
      tag: true,
      label,
      versionCapabilityCopy: VERSION_CAPABILITY_COPY,
      action: async () => {
        await request(api(path), {
          method: "POST",
          body: JSON.stringify(body),
        });
        await refresh();
      },
    });
  }

  function toggleMonitor() {
    // Turning ON sends the selected scope; turning OFF omits it (scope is irrelevant to an unmonitor).
    void act(
      "monitor",
      monitorRequestBody(kind, remoteIds, !monitored, monitored ? undefined : selectedScope),
      "update monitoring",
    );
  }

  function chooseScope(scope: MonitorScope) {
    setSelectedScope(scope);
    // If already monitoring, re-apply so the new scope reflects into Whisparr right away.
    if (monitored) {
      void act(
        "monitor",
        monitorRequestBody(kind, remoteIds, true, scope),
        "update the monitor scope",
      );
    }
  }

  const itemBase =
    "flex w-full items-center gap-2 rounded-md px-3 py-2 text-left text-sm text-foreground transition-colors hover:bg-card focus:bg-card focus:outline-none disabled:cursor-not-allowed disabled:opacity-60";

  return createPortal(
    <div
      ref={menuRef}
      role="menu"
      aria-label="Whisparr actions"
      style={{
        position: "fixed",
        top: pos?.top ?? -9999,
        left: pos?.left ?? -9999,
        width: MENU_WIDTH,
        zIndex: 50,
      }}
      className="flex flex-col gap-1 rounded-lg border border-border bg-background p-1"
    >
      {/* Brand header — the Whisparr logo + name marks OUR menu (user rule). */}
      <div className="flex items-center gap-2 px-3 py-2">
        <WhisparrLogo className="h-4 w-4 text-secondary" />
        <span className="text-xs font-semibold uppercase tracking-wide text-secondary">
          Whisparr
        </span>
      </div>

      {error !== null && (
        <div role="alert" className="border-t border-border px-3 py-2">
          <StatusText kind="error">{error}</StatusText>
        </div>
      )}

      <button
        type="button"
        role="menuitemcheckbox"
        aria-checked={monitored}
        disabled={monitorAffordance.disabled}
        title={monitorAffordance.title}
        aria-label={monitorAffordance.ariaLabel}
        onClick={toggleMonitor}
        className={itemBase}
      >
        <span className="flex h-4 w-4 items-center justify-center">
          {monitored && <Check className="h-4 w-4 text-accent" />}
        </span>
        <span className="flex-1">{menu.monitorLabel}</span>
        {busy && <Loader className="h-3 w-3 animate-spin text-secondary" />}
      </button>

      {/* The reason on screen, not only on hover: a status region, never the alert region above — a pre-click
          reason is not a failed attempt. One line covers every item the menu dims. */}
      {configSentence !== null && (
        <div role="status" className="border-t border-border px-3 py-2">
          <StatusText kind="warning">{configSentence}</StatusText>
        </div>
      )}

      {/* Scope sub-options — the "how" of the Monitor toggle; a radio group so exactly one is chosen. The
          visible heading is single-sourced from monitorLogic and framed as the NEXT action's scope (Whisparr
          stores no scope to reflect); the radiogroup keeps its stable "Monitor scope" accessible name. */}
      <div className="mt-1 border-t border-border pt-1">
        <div className="px-3 py-1 text-xs font-medium text-secondary">{MONITOR_SCOPE_HEADING}</div>
        <div role="radiogroup" aria-label="Monitor scope" className="flex flex-col gap-1">
          {MONITOR_SCOPE_OPTIONS.map((opt) => {
            const checked = selectedScope === opt.value;
            // Scoped to the monitored leg only, mirroring the server's own create/flip discriminator: choosing a
            // scope while UNMONITORED just records the next action's scope locally, but while monitored it
            // re-applies immediately, which is a create the server refuses.
            const scopeAffordance = guardedControl({
              name: opt.label,
              configurationReason: monitored ? configRequirement : null,
              busy,
            });
            return (
              <button
                key={opt.value}
                type="button"
                role="menuitemradio"
                aria-checked={checked}
                disabled={scopeAffordance.disabled}
                title={scopeAffordance.title}
                aria-label={scopeAffordance.ariaLabel}
                onClick={() => {
                  chooseScope(opt.value);
                }}
                className={itemBase}
              >
                <span className="flex h-4 w-4 items-center justify-center">
                  {checked ? (
                    <CircleDot className="h-4 w-4 text-accent" />
                  ) : (
                    <Circle className="h-4 w-4 text-secondary" />
                  )}
                </span>
                <span className="flex-1">
                  <span className="block">{opt.label}</span>
                  <span className="block text-xs text-secondary">{opt.description}</span>
                </span>
              </button>
            );
          })}
        </div>
        {/* Single-sourced next-action + escalation note (monitorLogic.MONITOR_SCOPE_HELP): raising the scope
            queues the back-catalogue, lowering it (or unmonitoring) leaves already-monitored scenes as they are. */}
        <p className="px-3 pt-1 text-xs text-secondary">{MONITOR_SCOPE_HELP}</p>
      </div>

      {/* Bulk actions — apply only while monitored (quiet by default). */}
      {menu.showBulk && (
        <div className="mt-1 border-t border-border pt-1">
          {addSupported && (
            <button
              type="button"
              role="menuitem"
              disabled={addAllAffordance.disabled}
              title={addAllAffordance.title}
              aria-label={addAllAffordance.ariaLabel}
              onClick={() => {
                if (coveEntityId !== null) {
                  void act(
                    "bulk-add-missing",
                    bulkAddMissingBody(kind, coveEntityId),
                    "add all missing scenes",
                  );
                }
              }}
              className={itemBase}
            >
              <PlusCircle className="h-4 w-4 text-secondary" />
              <span className="flex-1">Add all missing</span>
              {busy && <Loader className="h-3 w-3 animate-spin text-secondary" />}
            </button>
          )}
          {ownedImportSupported && (
            <button
              type="button"
              role="menuitem"
              disabled={busy || coveEntityId === null}
              title={
                coveEntityId === null
                  ? "This entity has no Cove id, so its owned scenes can't be enumerated."
                  : "Mark scenes you already own as present in Whisparr — imports the existing file in place (no grab, no move). Needs Cove and Whisparr to share storage (see Library path)."
              }
              onClick={() => {
                if (coveEntityId !== null) {
                  void act(
                    "reflect-owned",
                    reflectOwnedBody(kind, coveEntityId),
                    "reflect owned scenes",
                  );
                }
              }}
              className={itemBase}
            >
              <FileCheck2 className="h-4 w-4 text-secondary" />
              <span className="flex-1">Reflect owned in Whisparr</span>
              {busy && <Loader className="h-3 w-3 animate-spin text-secondary" />}
            </button>
          )}
          <button
            type="button"
            role="menuitem"
            disabled={busy}
            title="Search Whisparr for every monitored scene on this entity"
            onClick={() => {
              void act(
                "bulk-search-monitored",
                bulkSearchMonitoredBody(kind, remoteIds),
                "search all monitored scenes",
              );
            }}
            className={itemBase}
          >
            <Search className="h-4 w-4 text-secondary" />
            <span className="flex-1">Search all monitored</span>
            {busy && <Loader className="h-3 w-3 animate-spin text-secondary" />}
          </button>
        </div>
      )}
    </div>,
    document.body,
  );
}
