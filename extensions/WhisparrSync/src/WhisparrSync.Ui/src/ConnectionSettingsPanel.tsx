/**
 * ConnectionSettingsPanel — the connection form: a Whisparr URL, an API key, a Whisparr-version selector,
 * a Test connection button, and (revealed after a successful test) the auto-populated Root folder + Quality
 * profile dropdowns. Wired to the extension's minimal-API endpoints via the SDK `request`.
 *
 * The backend returns a `result` discriminator (CONN-02); each failure class renders its own copy + icon +
 * color, and a non-v3 instance is refused with an amber advisory (VER-04). On success the version selector
 * auto-selects the detected version (CONN-04) and the root-folder / quality-profile lists load from the live
 * API (CONN-05) — no hand-typed paths or numeric ids.
 *
 * CONN-06: the API-key input is `type="password"` and is NEVER pre-filled from the server — the panel only
 * learns whether a key is stored (`hasApiKey`) and shows a "Key is set" pill; a blank field on save preserves
 * the stored key (write-only). After a successful test the webhook section reveals a ready-to-use URL with an
 * embedded secret (CONN-07) to copy or best-effort auto-register into Whisparr.
 */
import { useEffect, useState } from "react";
import { CheckCircle2, XCircle, AlertTriangle, Copy } from "lucide-react";
import { request } from "@cove/extension-sdk";
import { Badge, Button, Chip, Field, Select, Spinner, StatusText, TextInput } from "./primitives";
import { DEFAULT_OPTIONS, optionsFromServer, type WhisparrOptions } from "./options";
import {
  connectionCopy,
  selectorForDetected,
  type ConnectionResult,
  type ConnectionTone,
  type WhisparrVersion,
} from "./connectionResult";

const EXTENSION_ID = "com.alextomas955.whisparrsync";

interface TestConnectionResponse {
  result: ConnectionResult["kind"];
  version?: string | null;
  instanceName?: string | null;
  reason?: string | null;
  detected?: string | null;
}

interface RootFolder {
  Id: number;
  Path?: string | null;
}

interface QualityProfile {
  Id: number;
  Name?: string | null;
}

const VERSION_OPTIONS: readonly { value: WhisparrVersion; label: string }[] = [
  { value: "v3", label: "v3 (Eros)" },
  { value: "v2", label: "v2" },
];

// The disabled empty-state option shown before a successful test (not hidden — the user sees what populates).
const NOT_LOADED_OPTION = { value: "0", label: "Test the connection to load this" } as const;

/** POST the connect creds (in the body, never a query string — CONN-06) and return both lists. */
async function fetchLists(
  baseUrl: string,
  apiKey: string,
): Promise<{ folders: RootFolder[]; profiles: QualityProfile[] }> {
  const body = JSON.stringify({ BaseUrl: baseUrl, ApiKey: apiKey });
  const [folders, profiles] = await Promise.all([
    request<RootFolder[]>(`/extensions/${EXTENSION_ID}/rootfolders`, { method: "POST", body }),
    request<QualityProfile[]>(`/extensions/${EXTENSION_ID}/qualityprofiles`, {
      method: "POST",
      body,
    }),
  ]);
  return { folders, profiles };
}

/** Fetch the ready-to-use webhook URL (the server mints + persists the embedded secret on first read). */
async function fetchWebhookUrl(): Promise<string> {
  const resp = await request<{ url: string }>(`/extensions/${EXTENSION_ID}/webhook-url`);
  return resp.url;
}

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

export function ConnectionSettingsPanel() {
  const [baseUrl, setBaseUrl] = useState(DEFAULT_OPTIONS.BaseUrl);
  const [apiKey, setApiKey] = useState("");
  const [selectedVersion, setSelectedVersion] = useState<WhisparrVersion>(
    DEFAULT_OPTIONS.SelectedVersion,
  );
  const [rootFolderId, setRootFolderId] = useState(DEFAULT_OPTIONS.RootFolderId);
  const [qualityProfileId, setQualityProfileId] = useState(DEFAULT_OPTIONS.QualityProfileId);
  const [hasApiKey, setHasApiKey] = useState(false);
  const [autoDetected, setAutoDetected] = useState(false);

  const [testing, setTesting] = useState(false);
  const [result, setResult] = useState<ConnectionResult | null>(null);
  const [rootFolders, setRootFolders] = useState<RootFolder[]>([]);
  const [qualityProfiles, setQualityProfiles] = useState<QualityProfile[]>([]);
  const [listsLoaded, setListsLoaded] = useState(false);

  const [saved, setSaved] = useState<WhisparrOptions>(DEFAULT_OPTIONS);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedFlash, setSavedFlash] = useState(false);

  const [webhookUrl, setWebhookUrl] = useState("");
  const [copied, setCopied] = useState(false);
  const [registering, setRegistering] = useState(false);
  const [registerMsg, setRegisterMsg] = useState<string | null>(null);

  // Load the persisted options once on mount; if a key is already stored, populate the dropdowns from the
  // live API using the stored creds (an empty submitted key falls back to the stored one server-side).
  useEffect(() => {
    void (async () => {
      try {
        const opts = optionsFromServer(await request(`/extensions/${EXTENSION_ID}/options`));
        setBaseUrl(opts.BaseUrl);
        setSelectedVersion(opts.SelectedVersion);
        setRootFolderId(opts.RootFolderId);
        setQualityProfileId(opts.QualityProfileId);
        setHasApiKey(opts.hasApiKey);
        setSaved(opts);
        if (opts.hasApiKey && opts.BaseUrl) {
          const { folders, profiles } = await fetchLists(opts.BaseUrl, "");
          setRootFolders(folders);
          setQualityProfiles(profiles);
          setListsLoaded(true);
          setWebhookUrl(await fetchWebhookUrl());
        }
      } catch {
        // First run or unreachable: keep defaults; the dropdowns stay in their disabled empty state.
      }
    })();
  }, []);

  async function testConnection() {
    setTesting(true);
    setResult(null);
    try {
      const resp = await request<TestConnectionResponse>(
        `/extensions/${EXTENSION_ID}/test-connection`,
        {
          method: "POST",
          body: JSON.stringify({ baseUrl, apiKey }),
        },
      );
      switch (resp.result) {
        case "success": {
          setResult({
            kind: "success",
            instanceName: resp.instanceName ?? "Whisparr",
            version: resp.version ?? "unknown",
          });
          const detected = selectorForDetected(resp.version);
          if (detected) {
            setSelectedVersion(detected);
            setAutoDetected(true);
          }
          // Populate the dropdowns from the just-tested creds (not yet saved — the key is in memory only).
          try {
            const { folders, profiles } = await fetchLists(baseUrl, apiKey);
            setRootFolders(folders);
            setQualityProfiles(profiles);
            setListsLoaded(true);
            setWebhookUrl(await fetchWebhookUrl());
          } catch {
            setListsLoaded(false);
          }
          break;
        }
        case "badKey":
          setResult({ kind: "badKey" });
          break;
        case "notWhisparr":
          setResult({ kind: "notWhisparr" });
          break;
        case "versionMismatch":
          setResult({ kind: "versionMismatch", detected: resp.detected ?? "an unknown version" });
          setAutoDetected(false);
          break;
        default:
          setResult({ kind: "unreachable", url: baseUrl });
          break;
      }
    } catch {
      // A thrown request (the endpoint itself failed / the URL didn't answer) is the unreachable class;
      // the backend returns a 200 discriminator for every reachable-Whisparr outcome (bad key, HTML, etc.).
      setResult({ kind: "unreachable", url: baseUrl });
    } finally {
      setTesting(false);
    }
  }

  async function save() {
    setSaving(true);
    setSaveError(null);
    try {
      await request(`/extensions/${EXTENSION_ID}/options`, {
        method: "POST",
        body: JSON.stringify({
          BaseUrl: baseUrl,
          ApiKey: apiKey, // empty preserves the stored key server-side (write-only; CONN-06)
          SelectedVersion: selectedVersion,
          RootFolderId: rootFolderId,
          QualityProfileId: qualityProfileId,
        }),
      });
      const snapshot: WhisparrOptions = {
        BaseUrl: baseUrl,
        SelectedVersion: selectedVersion,
        RootFolderId: rootFolderId,
        QualityProfileId: qualityProfileId,
        hasApiKey: hasApiKey || apiKey.length > 0,
      };
      setSaved(snapshot);
      setHasApiKey(snapshot.hasApiKey);
      setApiKey(""); // never keep the key in the field after a save
      setSavedFlash(true);
      window.setTimeout(() => {
        setSavedFlash(false);
      }, 2000);
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : "unknown error");
    } finally {
      setSaving(false);
    }
  }

  async function copyWebhookUrl() {
    try {
      await navigator.clipboard.writeText(webhookUrl);
      setCopied(true);
      window.setTimeout(() => {
        setCopied(false);
      }, 1500);
    } catch {
      // Clipboard access denied (e.g. non-secure context): the URL is still visible to select manually.
    }
  }

  async function registerWebhook() {
    setRegistering(true);
    setRegisterMsg(null);
    try {
      const resp = await request<{ registered: boolean }>(
        `/extensions/${EXTENSION_ID}/register-webhook`,
        { method: "POST" },
      );
      setRegisterMsg(
        resp.registered
          ? "Registered ✓"
          : "Couldn't auto-register — paste the URL into Whisparr → Settings → Connections instead.",
      );
    } catch {
      setRegisterMsg(
        "Couldn't auto-register — paste the URL into Whisparr → Settings → Connections instead.",
      );
    } finally {
      setRegistering(false);
    }
  }

  const dirty =
    apiKey.length > 0 ||
    baseUrl !== saved.BaseUrl ||
    selectedVersion !== saved.SelectedVersion ||
    rootFolderId !== saved.RootFolderId ||
    qualityProfileId !== saved.QualityProfileId;

  const rootFolderOptions = listsLoaded
    ? [
        { value: "0", label: "Select a root folder" },
        ...rootFolders.map((f) => ({ value: String(f.Id), label: f.Path ?? `Folder ${f.Id}` })),
      ]
    : [NOT_LOADED_OPTION];

  const qualityProfileOptions = listsLoaded
    ? [
        { value: "0", label: "Select a quality profile" },
        ...qualityProfiles.map((p) => ({
          value: String(p.Id),
          label: p.Name ?? `Profile ${p.Id}`,
        })),
      ]
    : [NOT_LOADED_OPTION];

  return (
    <div className="space-y-4" style={{ paddingBottom: dirty ? "5rem" : undefined }}>
      <Field label="Whisparr URL" helper="The address of your Whisparr instance.">
        <TextInput value={baseUrl} onChange={setBaseUrl} placeholder="http://localhost:6969" mono />
      </Field>

      <Field label="API key" helper="Whisparr → Settings → General → API Key.">
        <div className="flex items-center gap-2">
          <input
            type="password"
            value={apiKey}
            placeholder={hasApiKey ? "Key is set — type to replace" : "Your Whisparr API key"}
            onChange={(e) => {
              setApiKey(e.target.value);
            }}
            className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
          />
          {hasApiKey && apiKey.length === 0 ? <Badge>Key is set</Badge> : null}
        </div>
      </Field>

      <Field
        label="Whisparr version"
        helper={autoDetected ? "Detected v3 (Eros) — change only if this is wrong." : undefined}
      >
        <div className="flex gap-2">
          {VERSION_OPTIONS.map((o) => (
            <Chip
              key={o.value}
              selected={selectedVersion === o.value}
              onClick={() => {
                setSelectedVersion(o.value);
                setAutoDetected(false);
              }}
            >
              {o.label}
            </Chip>
          ))}
        </div>
      </Field>

      <div className="flex items-center gap-3">
        <Button
          variant="primary"
          onClick={() => {
            void testConnection();
          }}
          disabled={testing}
        >
          {testing ? <Spinner /> : null}
          Test connection
        </Button>
        {testing ? <StatusText kind="muted">Testing…</StatusText> : null}
      </div>

      {result ? <ConnectionResultBanner result={result} /> : null}

      <Field
        label="Root folder"
        helper={listsLoaded ? "Where Whisparr stores this library's files." : undefined}
      >
        <Select
          value={String(rootFolderId)}
          onChange={(v) => {
            setRootFolderId(Number(v));
          }}
          options={rootFolderOptions}
          disabled={!listsLoaded}
        />
      </Field>

      <Field
        label="Quality profile"
        helper={listsLoaded ? "The quality profile new items are added with." : undefined}
      >
        <Select
          value={String(qualityProfileId)}
          onChange={(v) => {
            setQualityProfileId(Number(v));
          }}
          options={qualityProfileOptions}
          disabled={!listsLoaded}
        />
      </Field>

      {webhookUrl ? (
        <Field
          label="Webhook URL"
          helper="Paste this into Whisparr → Settings → Connections → Webhook (On Import). Or let us add it for you."
        >
          <div className="space-y-2">
            <input
              type="text"
              value={webhookUrl}
              readOnly
              className="w-full rounded-xl border border-border bg-card px-3 py-2 font-mono text-sm text-foreground focus:border-accent focus:outline-none"
            />
            <div className="flex items-center gap-3">
              <Button
                variant="ghost"
                onClick={() => {
                  void copyWebhookUrl();
                }}
              >
                <Copy className="h-4 w-4" />
                {copied ? "Copied" : "Copy webhook URL"}
              </Button>
              <Button
                variant="ghost"
                onClick={() => {
                  void registerWebhook();
                }}
                disabled={registering}
              >
                {registering ? <Spinner /> : null}
                Register in Whisparr
              </Button>
              {registerMsg ? <StatusText kind="muted">{registerMsg}</StatusText> : null}
            </div>
          </div>
        </Field>
      ) : null}

      {dirty ? (
        <div className="pointer-events-none fixed inset-x-0 bottom-0 z-50 flex justify-center px-4 py-4">
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
                  Couldn&apos;t save — {saveError}. Your changes are still here; try Save again.
                </StatusText>
              ) : (
                <>
                  <div className="text-sm font-semibold text-foreground">Unsaved changes</div>
                  <div className="mt-0.5 text-xs text-secondary">
                    Nothing is stored until you save.
                  </div>
                </>
              )}
            </div>
            <Button
              onClick={() => {
                void save();
              }}
              disabled={saving}
            >
              {saving ? <Spinner /> : null}
              Save
            </Button>
          </div>
        </div>
      ) : null}
    </div>
  );
}
