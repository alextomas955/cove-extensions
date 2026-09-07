/**
 * ImportWebhookSection — the "Import webhook" SectionCard, extracted out of the connection form.
 * It shows the ready-to-use webhook URL (secret embedded, minted + persisted server-side on first read) with
 * Copy URL + Register in Whisparr, and an honest status line. The URL is EDITABLE so an admin whose Cove address
 * differs from the one Whisparr can reach can correct the host (Register forwards the edited URL; the server keeps
 * only its origin and re-mints the token). Reachability is never guessed from the string — the always-shown helper
 * states the rule and the "no events received yet" / "last event" status is the real proof. Presentational — the
 * webhook URL, the copy/register state, and the last-event timestamp are owned by {@link ./SettingsPage}.
 *
 * "Registered" is sourced from the extension's own "Cove Whisparr Sync" connection on the SELECTED version's
 * instance (authoritative, carried in on the `registration` prop), with a received webhook event as extra
 * proof — an absent event never downgrades a live connection. The third state, `notChecked`, exists because an
 * instance Cove has not read yet is not evidence that the connector is missing.
 * There is deliberately NO path mapping: the import flow uses Whisparr's reported
 * path as-is, so Cove and Whisparr must see the library at the same path — the always-shown reachability helper
 * states the one thing a mis-set host actually needs.
 * Never color-only — each state pairs a StatusText with a lucide glyph. Host token classes only; no raw HTML.
 */
import { CheckCircle2, AlertTriangle, CircleHelp, Copy } from "lucide-react";
import { Button, Field, SectionCard, Spinner, StatusText } from "@cove-extensions/ui-shared";
import { relativeTime, ticksToEpochMs } from "./importLogLogic";
import { resolveWebhookStatus, type WebhookRegistrationState } from "./webhookLogic";

/** The webhook status + the always-shown host-reachability helper (fixed copy). */
function WebhookStatus({
  registration,
  lastEventTicks,
}: {
  registration: WebhookRegistrationState;
  lastEventTicks: number | null;
}) {
  const { state } = resolveWebhookStatus(registration, lastEventTicks);
  return (
    <div className="space-y-1">
      {state === "registered" ? (
        // Inline null check (not the resolver's hasEvents) so TS narrows lastEventTicks for ticksToEpochMs.
        lastEventTicks !== null ? (
          <div className="flex items-start gap-2">
            <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-green-400" />
            <StatusText kind="success">
              Registered · last event {relativeTime(ticksToEpochMs(lastEventTicks))}
            </StatusText>
          </div>
        ) : (
          <StatusText kind="muted">Registered · no events received yet</StatusText>
        )
      ) : state === "notChecked" ? (
        <div className="flex items-start gap-2">
          <CircleHelp className="mt-0.5 h-4 w-4 shrink-0 text-muted" />
          <StatusText kind="muted">
            Cove hasn&rsquo;t checked this instance yet — Test connection, or Save to switch to it,
            and this will report what Whisparr actually has.
          </StatusText>
        </div>
      ) : (
        <div className="flex items-start gap-2">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-400" />
          <StatusText kind="warning">
            Not registered yet — Register in Whisparr, or paste the URL into Whisparr → Settings →
            Connections → Webhook (On Import).
          </StatusText>
        </div>
      )}
      <StatusText kind="muted">
        This URL must be reachable by Whisparr, not from your browser. If Whisparr runs on another
        host or in a container, use the address it can reach (for example{" "}
        <span className="font-mono">http://host.docker.internal:5073</span>), not{" "}
        <span className="font-mono">localhost</span>.
      </StatusText>
    </div>
  );
}

export interface ImportWebhookSectionProps {
  webhookUrl: string;
  onWebhookUrlChange: (url: string) => void;
  copied: boolean;
  onCopy: () => void;
  registering: boolean;
  registerMsg: string | null;
  onRegister: () => void;
  /** The selected version's own registration answer; `notChecked` until that instance has been read. */
  registration: WebhookRegistrationState;
  lastEventTicks: number | null;
}

export function ImportWebhookSection({
  webhookUrl,
  onWebhookUrlChange,
  copied,
  onCopy,
  registering,
  registerMsg,
  onRegister,
  registration,
  lastEventTicks,
}: ImportWebhookSectionProps) {
  return (
    <SectionCard
      title="Import webhook"
      description="How Whisparr tells Cove a file was imported. Must be reachable by Whisparr — not from your browser."
    >
      {webhookUrl ? (
        <Field
          label="Webhook URL"
          helper="Paste this into Whisparr → Settings → Connections → Webhook (On Import). Or let us add it for you."
        >
          <div className="space-y-2">
            <input
              type="text"
              value={webhookUrl}
              onChange={(e) => {
                onWebhookUrlChange(e.target.value);
              }}
              spellCheck={false}
              aria-label="Webhook URL"
              className="w-full rounded-xl border border-border bg-card px-3 py-2 font-mono text-sm text-foreground focus:border-accent focus:outline-none"
            />
            <div className="flex items-center gap-3">
              <Button variant="ghost" onClick={onCopy}>
                <Copy className="h-4 w-4" />
                {copied ? "Copied" : "Copy URL"}
              </Button>
              <Button variant="ghost" onClick={onRegister} disabled={registering}>
                {registering ? <Spinner /> : null}
                Register in Whisparr
              </Button>
              {registerMsg ? <StatusText kind="muted">{registerMsg}</StatusText> : null}
            </div>
            <WebhookStatus registration={registration} lastEventTicks={lastEventTicks} />
          </div>
        </Field>
      ) : (
        <StatusText kind="muted">
          Test the connection above to reveal your ready-to-use webhook URL.
        </StatusText>
      )}
    </SectionCard>
  );
}
