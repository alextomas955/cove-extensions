/**
 * GuidedSetupBanner — the quiet-by-default library-wide advisory at the top of the settings page (§3.5).
 * It reads `/identity-health` on mount and, when N scenes have no connected-version provider id, shows an
 * AMBER advisory (attention, not error — distinct from the red sync-problem alert above it) naming the
 * count + provider and the next step ("Identify them in Cove"). When N === 0 it renders NOTHING — it is not
 * a persistent "all good" banner. It is dismissible for the session only (local state; nothing is persisted
 * server-side — the underlying condition governs it, not a stored preference).
 *
 * The guided-fix is a LINK to the Cove library, not a bulk job: identifying scenes is Cove's own
 * metadata operation, and this extension owns no bulk-identify endpoint (adding one is out of scope). So no
 * action-handler, no Job Drawer, and — critically — no `window.alert` is involved (C5/B3). "Recheck" re-reads
 * `/identity-health` so the count refreshes after the user identifies scenes in Cove elsewhere.
 */
import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, RefreshCw, X } from "lucide-react";
import { request } from "@cove/extension-sdk";
import {
  GUIDED_SETUP_HEADING,
  guidedSetupSummary,
  hasUnidentified,
  identityHealthFromServer,
  NO_IDENTITY_PROBLEMS,
  type IdentityHealth,
} from "./guidedSetupLogic";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const IDENTITY_HEALTH_PATH = `/extensions/${EXTENSION_ID}/identity-health`;

export interface GuidedSetupBannerProps {
  /** The connected version's identity-provider name, single-sourced by the page via providerNameFor. */
  provider: string;
  /** Only read `/identity-health` once a connection is configured; before that the count is meaningless. */
  enabled: boolean;
}

export function GuidedSetupBanner({ provider, enabled }: GuidedSetupBannerProps) {
  const [health, setHealth] = useState<IdentityHealth>(NO_IDENTITY_PROBLEMS);
  const [dismissed, setDismissed] = useState(false);
  const [rechecking, setRechecking] = useState(false);

  const loadHealth = useCallback(async () => {
    try {
      const raw = await request<unknown>(IDENTITY_HEALTH_PATH);
      setHealth(identityHealthFromServer(raw));
    } catch {
      // A failed read is not a settings error; stay quiet (treat as no known problem) rather than alarm.
      setHealth(NO_IDENTITY_PROBLEMS);
    }
  }, []);

  const read = useCallback(async () => {
    setRechecking(true);
    try {
      await loadHealth();
    } finally {
      setRechecking(false);
    }
  }, [loadHealth]);

  useEffect(() => {
    if (!enabled) {
      return;
    }
    // eslint-disable-next-line react-hooks/set-state-in-effect -- setHealth runs post-await, never sync in-effect
    void loadHealth();
  }, [enabled, loadHealth]);

  const summary = guidedSetupSummary(health, provider);
  // Quiet by default: nothing to fix, dismissed for the session, or not yet connected → render nothing.
  if (!enabled || dismissed || !hasUnidentified(health) || summary === null) {
    return null;
  }

  return (
    <div
      role="alert"
      className="flex items-start gap-3 rounded-2xl border border-amber-500/40 bg-amber-500/10 px-4 py-3"
    >
      <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-400" />
      <div className="min-w-0 flex-1 space-y-2">
        <p className="text-sm font-semibold text-foreground">{GUIDED_SETUP_HEADING}</p>
        <p className="text-sm text-secondary" style={{ fontVariantNumeric: "tabular-nums" }}>
          {summary}
        </p>
        <div className="flex flex-wrap items-center gap-2 pt-0.5">
          <a
            href={window.location.origin + "/videos"}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1.5 rounded-lg bg-accent px-3 py-1.5 text-sm font-medium text-white hover:bg-accent-hover"
          >
            Identify these scenes
          </a>
          <button
            type="button"
            onClick={() => {
              void read();
            }}
            disabled={rechecking}
            className="inline-flex items-center gap-1.5 rounded-lg border border-border bg-card px-3 py-1.5 text-sm font-medium text-secondary hover:text-foreground disabled:opacity-60"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${rechecking ? "animate-spin" : ""}`} aria-hidden />
            Recheck
          </button>
        </div>
      </div>
      <button
        type="button"
        onClick={() => {
          setDismissed(true);
        }}
        aria-label="Dismiss this advisory for now"
        className="shrink-0 rounded-md p-1 text-muted hover:text-foreground"
      >
        <X className="h-4 w-4" aria-hidden />
      </button>
    </div>
  );
}
