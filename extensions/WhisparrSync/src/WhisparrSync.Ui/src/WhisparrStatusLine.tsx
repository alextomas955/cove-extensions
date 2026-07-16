/**
 * WhisparrStatusLine — the single quiet status line under a studio/performer's stat tiles. It is
 * display-only (never a second monitor toggle — the action-row {@link ./WhisparrMonitorButton} is the one
 * control) and rides the native `*-detail-bottom` slot.
 *
 * It reads the entity from TOP-LEVEL props (`props.studio` / `props.performer`), NEVER `props.context.*`, and
 * shares the button's `/monitor-status` fetch through {@link ./monitorStatusStore} (one call per
 * page open). It renders ONLY when the entity is monitored: a small accent Whisparr glyph plus
 * "Monitored in Whisparr · X of Y scenes" when counts exist, or the bare "Monitored in Whisparr" when they
 * do not. When the entity is not monitored it renders nothing — quiet by default, nothing to say. It also
 * renders a warning line when the status fetch failed outright, regardless of monitored state — the sole
 * permanently-visible signal on this action row when Whisparr can't be reached at all.
 *
 * It shows only Whisparr-owned facts (its own present/catalog scene counts), never Cove's own metadata
 * (Whisparr-only rule). Styling uses host Tailwind token classes only.
 */
import { AlertTriangle } from "lucide-react";
import { StatusText } from "@cove-ext/ui-shared";
import { WhisparrLogo } from "./WhisparrLogo";
import {
  MONITORED_LABEL,
  WHISPARR_UNAVAILABLE_COPY,
  entityKindOf,
  entityOf,
  hasCounts,
  shouldShowStatusLine,
  statusLineText,
  type SlotProps,
} from "./monitorLogic";
import { useMonitorStatus } from "./monitorStatusStore";

export function WhisparrStatusLine(props: SlotProps) {
  const kind = entityKindOf(props);
  const entity = entityOf(props);
  const remoteIds = entity?.remoteIds ?? [];
  const { state } = useMonitorStatus(kind, remoteIds);

  // The one permanently-visible signal on this action row when Whisparr is unreachable — the sibling
  // monitor button only has a hover-only tooltip for this outcome.
  if (state.error) {
    return (
      <div role="status" aria-live="polite" className="flex items-center gap-2 text-sm">
        <AlertTriangle className="h-4 w-4 text-amber-400" />
        <StatusText kind="warning">{WHISPARR_UNAVAILABLE_COPY}</StatusText>
      </div>
    );
  }

  const status = state.status;
  // Quiet by default: render nothing unless the entity is actually monitored.
  if (!shouldShowStatusLine(status) || status === null) return null;

  const text = hasCounts(status)
    ? statusLineText(status.scenesPresent, status.scenesTotal)
    : MONITORED_LABEL;

  return (
    <div className="flex items-center gap-2 text-sm text-secondary">
      <WhisparrLogo className="h-4 w-4" />
      <span>{text}</span>
    </div>
  );
}
