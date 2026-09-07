/**
 * WhisparrMonitorButton — the studio/performer action-cluster control. It rides the native action-row slot
 * (`studio-detail-actions` / `performer-detail-actions`) and renders ONE control next to Edit: the WhisparrLogo
 * button, which simply OPENS our own {@link ./WhisparrMenu} popover. There is no bare-logo monitor toggle and no
 * separate ⋮ trigger — everything (the Monitor toggle, its scope choice, the bulk actions, the status line) lives
 * inside the one menu, so the control the user clicks always leads to the same place.
 *
 * The logo stays tinted accent while the entity is monitored (Cove's selected-chip idiom) so the state is still
 * glanceable at a glance, even closed. It reads its entity from TOP-LEVEL props (`props.studio` /
 * `props.performer`), NEVER `props.context.*`, and shares the entity's monitor state through
 * {@link ./monitorStatusStore} (one `/monitor-status` call per page open, shared with the status line and the
 * menu); a mutation inside the menu refreshes that shared state so the tint, the *-detail-bottom status line, and
 * the menu update in lockstep.
 *
 * The button disables quietly (a muted icon + honest tooltip, never an alert) only for causes that make EVERY
 * item behind it unusable: while loading, when the entity has no id for the connected Whisparr version, when the
 * connected version does not offer the capability for this entity (a performer on v2 — the tooltip reads
 * monitorLogic's version-capability copy), or when Whisparr is not reachable. An unusable required SETTING is
 * deliberately not one of them — that produces a mixed menu, so it is refused per item inside
 * {@link ./WhisparrMenu} instead. A v2 studio whose entity carries a ThePornDB id is fully ENABLED. Styling uses
 * host Tailwind token classes only (no hex, no CSS bundle) so check-classes passes.
 */
import { useCallback, useRef, useState } from "react";
import { StatusText } from "@cove-extensions/ui-shared";
import { WhisparrLogo } from "../common/ui/WhisparrLogo";
import { entityKindOf, entityOf, monitorButtonTitle, type SlotProps } from "./monitorLogic";
import { missingIdMessage } from "../common/lib/identityGuardLogic";
import { BUILD_CAPABILITY_COPY } from "../common/lib/whisparrCopy";
import { useMonitorStatus } from "./monitorStatusStore";
import { WhisparrMenu } from "./WhisparrMenu";

export function WhisparrMonitorButton(props: SlotProps) {
  const kind = entityKindOf(props);
  const entity = entityOf(props);
  const remoteIds = entity?.remoteIds ?? [];
  const coveEntityId = entity?.id ?? null;
  const { state } = useMonitorStatus(kind, remoteIds);
  const [open, setOpen] = useState(false);
  const menuTriggerRef = useRef<HTMLButtonElement>(null);

  // Stable identity: WhisparrMenu's setup effect keys on onClose, so a fresh function each render would
  // re-run it (re-focusing the first item, re-binding listeners) on every store emit while the menu is open.
  const closeMenu = useCallback(() => {
    setOpen(false);
    menuTriggerRef.current?.focus();
  }, []);

  // v2 (Sonarr) has no performer entity — the control stays visible but disabled, its tooltip stating where the
  // capability IS available (never a "migrate to v3" nudge). Both versions are first-class, so hiding it would
  // wrongly read as "this entity has no Whisparr integration at all".
  const monitored = state.status?.monitored === true;
  const blocked = state.noIdentity || state.error || state.capabilityUnavailable;
  // An unset required setting deliberately does NOT disable this trigger. It is the only door into the menu, and
  // the menu still has something to show behind it — the entity's status, and choosing a scope while unmonitored,
  // which is recorded locally and reaches Whisparr only on the next action. So the refusal is carried per item
  // inside the menu; the trigger refuses only for causes that make EVERY item unusable — no identity, an
  // unsupported entity for the connected version, or an unreachable instance.
  const disabled = kind === null || state.loading || blocked || state.unsupported;

  // The missing-id guard: when the entity has no provider id, the button is disabled with a
  // version-aware, provider-named reason — shown as the tooltip/aria-label AND as a visible inline line
  // beneath the control (not hover-only). The wording is single-sourced in identityGuardLogic.
  const noun = kind === "performer" ? "performer" : "studio";
  const guardReason = state.noIdentity
    ? missingIdMessage(noun, state.provider)
    : state.capabilityUnavailable
      ? BUILD_CAPABILITY_COPY
      : null;

  const title =
    guardReason ??
    monitorButtonTitle({
      kind,
      loading: state.loading,
      noIdentity: state.noIdentity,
      unsupported: state.unsupported,
      error: state.error,
      monitored,
    });

  // Tinted when monitored (accent tint on the background, matching Cove's selected-chip idiom), muted otherwise.
  const toggleColor = monitored
    ? "border-accent bg-accent/15 text-accent"
    : "border-border bg-card text-secondary";

  return (
    <div className="inline-flex flex-col gap-1">
      <div className="inline-flex items-center gap-1">
        <button
          ref={menuTriggerRef}
          type="button"
          onClick={() => {
            setOpen((v) => !v);
          }}
          disabled={disabled}
          title={title}
          aria-label={title}
          aria-haspopup="menu"
          aria-expanded={open}
          className={`inline-flex h-10 w-10 items-center justify-center rounded-lg border transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-60 ${toggleColor}`}
        >
          <WhisparrLogo className="h-5 w-5" />
        </button>

        {open && kind !== null && !state.unsupported && (
          <WhisparrMenu
            kind={kind}
            remoteIds={remoteIds}
            coveEntityId={coveEntityId}
            triggerRef={menuTriggerRef}
            onClose={closeMenu}
          />
        )}
      </div>

      {guardReason !== null && (
        <div role="status" style={{ maxWidth: 240 }}>
          <StatusText kind="warning">{guardReason}</StatusText>
        </div>
      )}
    </div>
  );
}
