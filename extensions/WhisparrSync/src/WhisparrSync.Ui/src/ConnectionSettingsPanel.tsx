/**
 * ConnectionSettingsPanel — the "Connection" SectionCard: Whisparr URL, API key, the
 * Whisparr-version selector, and a Test connection button with its classified result line. Presentational —
 * the shared form state + the /options save live in {@link ./SettingsPage} (a single save spans Connection and
 * Add-defaults because the server writes QualityProfileId unconditionally, so one save must carry every field).
 * The webhook block and the quality-profile dropdown moved out to {@link ./ImportWebhookSection} and
 * {@link ./AddDefaultsSection}.
 *
 * The API-key input is `type="password"` and is NEVER pre-filled from the server — the panel only
 * learns whether a key is stored (`hasApiKey`) and shows a "KEY IS SET" micro-badge; a blank field on save
 * preserves the stored key (write-only). The backend returns a `result` discriminator; each failure
 * class renders its own copy + icon + color, and a non-v3 instance is refused with an amber advisory.
 */
import { CheckCircle2, XCircle, AlertTriangle } from "lucide-react";
import { Badge, Button, Field, SectionCard, Spinner, StatusText } from "@cove-ext/ui-shared";
import {
  connectionCopy,
  type ConnectionResult,
  type ConnectionTone,
  type WhisparrVersion,
} from "./connectionResult";

const VERSION_OPTIONS: readonly { value: WhisparrVersion; label: string }[] = [
  { value: "v3", label: "v3 (Eros)" },
  { value: "v2", label: "v2" },
];

function ToneIcon({ tone }: { tone: ConnectionTone }) {
  if (tone === "success") {
    return <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-green-400" />;
  }
  if (tone === "error") {
    return <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-red-400" />;
  }
  return <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-400" />;
}

/**
 * The distinct-state result banner beneath Test connection: a lucide status icon + StatusText (+ a version
 * Badge on success) per classified result. `role="status" aria-live="polite"` announces the outcome.
 */
function ConnectionResultBanner({ result }: { result: ConnectionResult }) {
  const copy = connectionCopy(result);
  return (
    <div role="status" aria-live="polite" className="flex items-start gap-2">
      <ToneIcon tone={copy.tone} />
      <StatusText kind={copy.tone}>{copy.message}</StatusText>
      {result.kind === "success" && result.version ? <Badge mono>{result.version}</Badge> : null}
    </div>
  );
}

export interface ConnectionSettingsPanelProps {
  baseUrl: string;
  onBaseUrl: (value: string) => void;
  apiKey: string;
  onApiKey: (value: string) => void;
  hasApiKey: boolean;
  selectedVersion: WhisparrVersion;
  onVersion: (value: WhisparrVersion) => void;
  autoDetected: boolean;
  /** True while a version switch is repopulating the other instance's dropdowns — renders a loading line. */
  switching: boolean;
  /** A transient note under the version selector after a switch (which connection loaded / that Save applies). */
  switchNote: string | null;
  testing: boolean;
  result: ConnectionResult | null;
  /** A saved connection whose live read failed on page load, with no test run yet — shows a muted prompt to retry. */
  unreachableOnLoad: boolean;
  onTest: () => void;
}

export function ConnectionSettingsPanel({
  baseUrl,
  onBaseUrl,
  apiKey,
  onApiKey,
  hasApiKey,
  selectedVersion,
  onVersion,
  autoDetected,
  switching,
  switchNote,
  testing,
  result,
  unreachableOnLoad,
  onTest,
}: ConnectionSettingsPanelProps) {
  return (
    <SectionCard title="Connection" description="How Cove reaches your Whisparr instance.">
      <Field
        label="Whisparr version"
        helper={
          autoDetected
            ? `Detected ${
                VERSION_OPTIONS.find((o) => o.value === selectedVersion)?.label ?? selectedVersion
              } — change only if this is wrong.`
            : "Pick your Whisparr generation, then enter that instance's URL and key below."
        }
      >
        <div className="grid grid-cols-2 gap-3">
          {VERSION_OPTIONS.map((o) => {
            const active = selectedVersion === o.value;
            return (
              <button
                key={o.value}
                type="button"
                aria-pressed={active}
                onClick={() => {
                  if (!active) onVersion(o.value);
                }}
                className={`flex items-center justify-between rounded-xl border px-4 py-3 text-left transition-colors ${
                  active
                    ? "border-accent bg-accent/10"
                    : "border-border bg-card hover:border-accent/50"
                }`}
              >
                <span className="flex items-center gap-2.5">
                  <span
                    className={`flex h-4 w-4 items-center justify-center rounded-full border ${
                      active ? "border-accent" : "border-border"
                    }`}
                  >
                    {active ? <span className="h-2 w-2 rounded-full bg-accent" /> : null}
                  </span>
                  <span className="text-sm font-medium text-foreground">{o.label}</span>
                </span>
                {active ? (
                  <span className="text-xs font-semibold uppercase tracking-wide text-accent">
                    Active
                  </span>
                ) : (
                  <span className="text-xs font-medium text-accent">Switch &rarr;</span>
                )}
              </button>
            );
          })}
        </div>
        {switching ? (
          <div className="mt-2 flex items-center gap-2">
            <Spinner />
            <StatusText kind="muted">Loading this version&rsquo;s saved connection…</StatusText>
          </div>
        ) : switchNote ? (
          <div className="mt-2">
            <StatusText kind="muted">{switchNote}</StatusText>
          </div>
        ) : null}
      </Field>

      <Field label="Whisparr URL" helper="The address of your Whisparr instance.">
        <input
          type="text"
          value={baseUrl}
          placeholder="http://localhost:6969"
          onChange={(e) => {
            onBaseUrl(e.target.value);
          }}
          className="w-full rounded-xl border border-border bg-card px-3 py-2 font-mono text-sm text-foreground focus:border-accent focus:outline-none"
        />
      </Field>

      <Field label="API key" helper="Whisparr → Settings → General → API Key.">
        <div className="flex items-center gap-2">
          <input
            type="password"
            value={apiKey}
            placeholder={hasApiKey ? "Key is set — type to replace" : "Your Whisparr API key"}
            onChange={(e) => {
              onApiKey(e.target.value);
            }}
            className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
          />
          {hasApiKey && apiKey.length === 0 ? <Badge>Key is set</Badge> : null}
        </div>
      </Field>

      <div className="flex items-center gap-3">
        <Button variant="primary" onClick={onTest} disabled={testing}>
          {testing ? <Spinner /> : null}
          Test connection
        </Button>
        {testing ? <StatusText kind="muted">Testing…</StatusText> : null}
      </div>

      {result ? (
        <ConnectionResultBanner result={result} />
      ) : unreachableOnLoad ? (
        // A saved connection that didn't respond on page load (Whisparr down), before any Test this session.
        // Muted, not the red post-Test error banner — nothing the user just did failed.
        <div role="status" className="flex items-start gap-2">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-400" />
          <StatusText kind="warning">
            Your saved connection didn&apos;t respond just now — Test connection to check whether
            Whisparr is reachable.
          </StatusText>
        </div>
      ) : null}
    </SectionCard>
  );
}
