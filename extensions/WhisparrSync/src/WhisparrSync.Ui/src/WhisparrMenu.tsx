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
import { request, ApiError } from "@cove/extension-sdk";
import { StatusText } from "@cove-ext/ui-shared";
import { WhisparrLogo } from "./WhisparrLogo";
import {
  DEFAULT_MONITOR_SCOPE,
  MONITOR_SCOPE_HEADING,
  MONITOR_SCOPE_HELP,
  MONITOR_SCOPE_OPTIONS,
  VERSION_CAPABILITY_COPY,
  monitorRequestBody,
  type EntityKind,
  type MonitorScope,
  type RemoteIdPair,
} from "./monitorLogic";
import {
  bulkAddMissingBody,
  bulkSearchMonitoredBody,
  menuItemsState,
  reflectOwnedBody,
} from "./sceneActionsLogic";
import { actionFailureCopy } from "./actionFailureLogic";
import { EXTENSION_ID, useMonitorStatus } from "./monitorStatusStore";

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
  const menuRef = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);
  const [busy, setBusy] = useState(false);
  const [selectedScope, setSelectedScope] = useState<MonitorScope>(DEFAULT_MONITOR_SCOPE);
  const [error, setError] = useState<string | null>(null);

  const menu = menuItemsState(state.status);
  const monitored = menu.monitorChecked;
  const addSupported = state.status?.addSupported ?? false;
  const ownedImportSupported = state.status?.ownedImportSupported ?? false;

  // Anchor to the trigger before paint so the popover never flashes at the wrong spot. Right-align to the
  // button (the action row sits top-right) and clamp to the viewport's left edge.
  useLayoutEffect(() => {
    const el = triggerRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const left = Math.max(8, rect.right - MENU_WIDTH);
    setPos({ top: rect.bottom + 6, left });
  }, [triggerRef]);

  // Focus the first item on open, and wire outside-click + Escape + arrow-key navigation. Listeners are on the
  // capture phase so the menu wins over host handlers; cleanup removes them when the menu unmounts (closes).
  useLayoutEffect(() => {
    const items = () =>
      Array.from(menuRef.current?.querySelectorAll<HTMLElement>('[role^="menuitem"]') ?? []);
    items()[0]?.focus();

    function onPointerDown(e: PointerEvent) {
      const target = e.target as Node;
      if (menuRef.current?.contains(target)) return;
      if (triggerRef.current?.contains(target)) return;
      onClose();
    }
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") {
        e.stopPropagation();
        onClose();
        return;
      }
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        const list = items();
        if (list.length === 0) return;
        const current = list.indexOf(document.activeElement as HTMLElement);
        const next =
          e.key === "ArrowDown"
            ? (current + 1) % list.length
            : (current - 1 + list.length) % list.length;
        list[next]?.focus();
      }
    }

    document.addEventListener("pointerdown", onPointerDown, true);
    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown, true);
      document.removeEventListener("keydown", onKeyDown, true);
    };
  }, [onClose, triggerRef]);

  // POST one mutation, then refresh the shared monitor state (logo tint + status line + this menu in lockstep).
  // The menu stays open so the user sees the checkbox / status update. A failure now shows the caught error via
  // `error`/`StatusText` instead of relying on the tooltip alone.
  async function act(path: string, body: unknown, label: string) {
    setBusy(true);
    setError(null);
    try {
      await request(`/extensions/${EXTENSION_ID}/${path}`, {
        method: "POST",
        body: JSON.stringify(body),
      });
      await refresh();
    } catch (err) {
      const respStatus = err instanceof ApiError ? err.status : -1;
      const respBody = err instanceof ApiError ? err.body : null;
      setError(actionFailureCopy(label, respStatus, respBody, VERSION_CAPABILITY_COPY));
    } finally {
      setBusy(false);
    }
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
        disabled={busy}
        onClick={toggleMonitor}
        className={itemBase}
      >
        <span className="flex h-4 w-4 items-center justify-center">
          {monitored && <Check className="h-4 w-4 text-accent" />}
        </span>
        <span className="flex-1">{menu.monitorLabel}</span>
        {busy && <Loader className="h-3 w-3 animate-spin text-secondary" />}
      </button>

      {/* Scope sub-options — the "how" of the Monitor toggle; a radio group so exactly one is chosen. The
          visible heading is single-sourced from monitorLogic and framed as the NEXT action's scope (Whisparr
          stores no scope to reflect); the radiogroup keeps its stable "Monitor scope" accessible name. */}
      <div className="mt-1 border-t border-border pt-1">
        <div className="px-3 py-1 text-xs font-medium text-secondary">{MONITOR_SCOPE_HEADING}</div>
        <div role="radiogroup" aria-label="Monitor scope" className="flex flex-col gap-1">
          {MONITOR_SCOPE_OPTIONS.map((opt) => {
            const checked = selectedScope === opt.value;
            return (
              <button
                key={opt.value}
                type="button"
                role="menuitemradio"
                aria-checked={checked}
                disabled={busy}
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
              disabled={busy || coveEntityId === null}
              title={
                coveEntityId === null
                  ? "This entity has no Cove id, so its missing scenes can't be enumerated."
                  : "Register every Cove scene not yet in Whisparr (no grab)"
              }
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
