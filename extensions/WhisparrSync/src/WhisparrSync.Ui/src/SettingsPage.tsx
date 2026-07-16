/**
 * SettingsPage — the component the host mounts inside the "Whisparr Sync" SETTINGS TAB. It owns the shared
 * connection + add-defaults form state and the single floating save bar, and stacks the six sections in a
 * fixed order: Connection · Library path · Import webhook · Add defaults · Reconciliation · Import activity.
 *
 * A SINGLE save spans Connection + Library-path + Add-defaults on purpose: the server's SaveOptions writes
 * RootFolderId / QualityProfileId UNCONDITIONALLY (they are non-nullable), so a per-section partial save would
 * wipe the stored root/quality selection. One save posting every field is the only safe shape. The API key is
 * never modeled beyond `hasApiKey`; a blank key field preserves the stored key server-side.
 *
 * The host passes `{ onNavigate }`; this UI does not navigate, so it ignores it. Styling uses host Tailwind
 * token classes only (no hex, no CSS bundle).
 */
import { useEffect, useState } from "react";
import { AlertTriangle } from "lucide-react";
import { request } from "@cove/extension-sdk";
import { Button, Spinner, StatusText } from "@cove-extensions/ui-shared";
import { ConnectionSettingsPanel } from "./ConnectionSettingsPanel";
import { ImportWebhookSection } from "./ImportWebhookSection";
import { AddDefaultsSection } from "./AddDefaultsSection";
import { ImportLogSection } from "./ImportLogSection";
import { ReconciliationSection } from "./ReconciliationSection";
import {
  DEFAULT_OPTIONS,
  optionsFromServer,
  type ConnectionView,
  type WhisparrOptions,
} from "./options";
import {
  selectorForDetected,
  type ConnectionResult,
  type WhisparrVersion,
} from "./connectionResult";
import { type ImportLogRow } from "./importLogLogic";
import {
  NO_SYNC_PROBLEMS,
  hasSyncProblem,
  syncHealthFromServer,
  syncProblemSummary,
  type SyncHealth,
} from "./syncHealthLogic";
import { clearMonitorStatusCache } from "./monitorStatusStore";
import { clearSceneStatusCaches } from "./sceneStatusStore";
import { clearCardStatusCache } from "./cardStatusStore";
import { clearEntityCardStatusCache } from "./entityCardStatusStore";
import { clearEntityLibrarySummaryCache } from "./entityLibrarySummaryStore";

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

// The disabled empty-state option shown before a successful test (not hidden — the user sees what populates).
const NOT_LOADED_OPTION = { value: "0", label: "Test the connection to load this" } as const;

/** POST the connect creds (in the body, never a query string) and return both dropdown lists. */
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

/** The most recent webhook import ticks + the sync-health signal, from one `/import-log` read. */
async function fetchImportStatus(): Promise<{ lastTicks: number | null; syncHealth: SyncHealth }> {
  try {
    const resp = await request<{ entries: ImportLogRow[]; syncHealth?: unknown }>(
      `/extensions/${EXTENSION_ID}/import-log`,
    );
    const ticks = resp.entries.filter((e) => e.source === "webhook").map((e) => e.utcTicks);
    return {
      lastTicks: ticks.length > 0 ? Math.max(...ticks) : null,
      syncHealth: syncHealthFromServer(resp.syncHealth),
    };
  } catch {
    return { lastTicks: null, syncHealth: NO_SYNC_PROBLEMS }; // a failed log read is not a settings error
  }
}

/** Order-insensitive tag-list equality for the dirty check. */
function sameTags(a: string[], b: string[]): boolean {
  if (a.length !== b.length) return false;
  const sortedA = [...a].sort();
  const sortedB = [...b].sort();
  return sortedA.every((v, i) => v === sortedB[i]);
}

export function SettingsPage() {
  const [baseUrl, setBaseUrl] = useState(DEFAULT_OPTIONS.BaseUrl);
  const [apiKey, setApiKey] = useState("");
  const [selectedVersion, setSelectedVersion] = useState<WhisparrVersion>(
    DEFAULT_OPTIONS.SelectedVersion,
  );
  const [rootFolderId, setRootFolderId] = useState(DEFAULT_OPTIONS.RootFolderId);
  const [qualityProfileId, setQualityProfileId] = useState(DEFAULT_OPTIONS.QualityProfileId);
  const [tags, setTags] = useState<string[]>(DEFAULT_OPTIONS.TagsOnAdd);
  const [monitorNew, setMonitorNew] = useState(DEFAULT_OPTIONS.MonitorNewByDefault);
  const [allowUpgrades, setAllowUpgrades] = useState(DEFAULT_OPTIONS.AllowQualityUpgrades);
  const [hasApiKey, setHasApiKey] = useState(false);
  const [autoDetected, setAutoDetected] = useState(false);
  // Per-version saved connections, so toggling v3/v2 restores that instance's URL/key/root/profile.
  const [savedConnections, setSavedConnections] = useState<Record<string, ConnectionView>>({});
  // Transient feedback for a version switch: a spinner while the dropdowns refetch, then a one-line note.
  const [switchingVersion, setSwitchingVersion] = useState(false);
  const [versionNote, setVersionNote] = useState<string | null>(null);

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
  const [registered, setRegistered] = useState(false);
  const [lastWebhookEventTicks, setLastWebhookEventTicks] = useState<number | null>(null);
  const [syncHealth, setSyncHealth] = useState<SyncHealth>(NO_SYNC_PROBLEMS);

  // Load the persisted options once on mount; if a key is already stored, populate the dropdowns + webhook from
  // the live API using the stored creds (an empty submitted key falls back to the stored one server-side).
  useEffect(() => {
    void (async () => {
      try {
        const opts = optionsFromServer(await request(`/extensions/${EXTENSION_ID}/options`));
        setBaseUrl(opts.BaseUrl);
        setSelectedVersion(opts.SelectedVersion);
        setRootFolderId(opts.RootFolderId);
        setQualityProfileId(opts.QualityProfileId);
        setTags(opts.TagsOnAdd);
        setMonitorNew(opts.MonitorNewByDefault);
        setAllowUpgrades(opts.AllowQualityUpgrades);
        setHasApiKey(opts.hasApiKey);
        setSavedConnections(opts.SavedConnections);
        setSaved(opts);
        if (opts.hasApiKey && opts.BaseUrl) {
          const { folders, profiles } = await fetchLists(opts.BaseUrl, "");
          setRootFolders(folders);
          setQualityProfiles(profiles);
          setListsLoaded(true);
          setWebhookUrl(await fetchWebhookUrl());
          const status = await fetchImportStatus();
          setLastWebhookEventTicks(status.lastTicks);
          setSyncHealth(status.syncHealth);
        }
      } catch {
        // First run or unreachable: keep defaults; the dropdowns stay in their disabled empty state.
      }
    })();
  }, []);

  async function testConnection() {
    setTesting(true);
    setResult(null);
    setVersionNote(null);
    try {
      const resp = await request<TestConnectionResponse>(
        `/extensions/${EXTENSION_ID}/test-connection`,
        { method: "POST", body: JSON.stringify({ baseUrl, apiKey }) },
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
      // A thrown request is the unreachable class; the backend returns a 200 discriminator for every
      // reachable-Whisparr outcome (bad key, HTML, etc.).
      setResult({ kind: "unreachable", url: baseUrl });
    } finally {
      setTesting(false);
    }
  }

  async function save() {
    setSaving(true);
    setSaveError(null);
    setVersionNote(null);
    // Whether this save changes the Whisparr version. The host draws the per-version manifest surfaces (scene
    // tab, per-version toolbars/rows/badges) ONCE on page load and does not re-request the manifest on a settings
    // save, so a version switch needs a reload to apply — otherwise the old version's surfaces linger (e.g. the
    // performer toolbar showing on v2, which has no performer entity).
    const versionChanged = selectedVersion !== saved.SelectedVersion;
    try {
      // One save posting EVERY field: the server writes RootFolderId/QualityProfileId unconditionally, so a
      // partial body would blank them. An empty ApiKey preserves the stored key (write-only).
      // The server echoes the redaction-safe options back — including the recomputed per-version
      // SavedConnections — so snapshot from the response rather than reconstructing it here.
      const snapshot = optionsFromServer(
        await request(`/extensions/${EXTENSION_ID}/options`, {
          method: "POST",
          body: JSON.stringify({
            BaseUrl: baseUrl,
            ApiKey: apiKey,
            SelectedVersion: selectedVersion,
            RootFolderId: rootFolderId,
            QualityProfileId: qualityProfileId,
            TagsOnAdd: tags,
            MonitorNewByDefault: monitorNew,
            AllowQualityUpgrades: allowUpgrades,
          }),
        }),
      );
      setSaved(snapshot);
      setSavedConnections(snapshot.SavedConnections);
      setHasApiKey(snapshot.hasApiKey);
      setApiKey(""); // never keep the key in the field after a save
      // A saved connection change (version/URL/key) makes the slot components' cached Whisparr status stale;
      // drop those module-level caches so the next studio/performer/scene view refetches against the new config.
      clearMonitorStatusCache();
      clearSceneStatusCaches();
      clearCardStatusCache();
      clearEntityCardStatusCache();
      clearEntityLibrarySummaryCache();
      if (versionChanged) {
        // Reload so the host re-fetches the manifest and the per-version surfaces update immediately.
        setVersionNote(
          `Switched to ${snapshot.SelectedVersion === "v2" ? "v2" : "v3 (Eros)"} — reloading to apply…`,
        );
        window.setTimeout(() => {
          window.location.reload();
        }, 700);
        return;
      }
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
      // Forward the shown (possibly hand-edited) URL so the server registers the host Whisparr can actually
      // reach; the server keeps only its origin and re-mints the token from the stored secret.
      const resp = await request<{ registered: boolean }>(
        `/extensions/${EXTENSION_ID}/register-webhook`,
        { method: "POST", body: JSON.stringify({ Url: webhookUrl }) },
      );
      setRegistered(resp.registered);
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
    qualityProfileId !== saved.QualityProfileId ||
    !sameTags(tags, saved.TagsOnAdd) ||
    monitorNew !== saved.MonitorNewByDefault ||
    allowUpgrades !== saved.AllowQualityUpgrades;

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

  // Switch the version selector: stash the current form connection, load the target version's saved
  // connection into the form, and repopulate its dropdowns. The active connection only changes on Save.
  async function handleVersionChange(v: WhisparrVersion) {
    if (v === selectedVersion) return;
    setAutoDetected(false);
    setResult(null);

    const stash: Record<string, ConnectionView> = {
      ...savedConnections,
      [selectedVersion]: {
        BaseUrl: baseUrl,
        RootFolderId: rootFolderId,
        QualityProfileId: qualityProfileId,
        hasApiKey: hasApiKey || apiKey.length > 0,
      },
    };
    setSavedConnections(stash);
    setSelectedVersion(v);

    const target = v in stash ? stash[v] : undefined;
    setBaseUrl(target?.BaseUrl ?? "");
    setRootFolderId(target?.RootFolderId ?? 0);
    setQualityProfileId(target?.QualityProfileId ?? 0);
    setHasApiKey(target?.hasApiKey ?? false);
    setApiKey(""); // blank = keep that version's saved key on Save

    const label = v === "v2" ? "v2" : "v3 (Eros)";
    if (!target?.hasApiKey || !target.BaseUrl) {
      // Never configured this version: blank the form for a fresh setup and say so.
      setRootFolders([]);
      setQualityProfiles([]);
      setListsLoaded(false);
      setSwitchingVersion(false);
      setVersionNote(
        `No saved ${label} connection yet — enter its URL and key, then Test connection.`,
      );
      return;
    }

    // Repopulate the root/profile dropdowns for the target instance. The server pairs the saved key with the
    // saved URL, so a blank key still authenticates against that version's own host.
    setSwitchingVersion(true);
    setVersionNote(null);
    try {
      const { folders, profiles } = await fetchLists(target.BaseUrl, "");
      setRootFolders(folders);
      setQualityProfiles(profiles);
      setListsLoaded(true);
      setVersionNote(`Loaded your saved ${label} connection — Save to switch to it.`);
    } catch {
      // The saved instance is unreachable or its key was rejected: keep the restored URL but empty the lists.
      setRootFolders([]);
      setQualityProfiles([]);
      setListsLoaded(false);
      setVersionNote(
        `Loaded your saved ${label} connection, but it isn't reachable right now — Test connection to retry.`,
      );
    } finally {
      setSwitchingVersion(false);
    }
  }

  return (
    <div className="space-y-6" style={{ paddingBottom: dirty ? "5rem" : undefined }}>
      {hasSyncProblem(syncHealth) ? (
        <div
          role="alert"
          className="flex items-start gap-3 rounded-2xl border border-red-500/40 bg-red-500/10 px-4 py-3"
        >
          <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-red-400" />
          <div className="min-w-0 space-y-1">
            <p className="text-sm font-semibold text-foreground">
              Sync problem — Cove can&rsquo;t find imported files
            </p>
            <p className="text-sm text-secondary">{syncProblemSummary(syncHealth)}</p>
            {syncHealth.samplePaths.length > 0 ? (
              <p
                className="truncate font-mono text-xs text-muted"
                title={syncHealth.samplePaths.join("\n")}
              >
                e.g. {syncHealth.samplePaths[0]}
              </p>
            ) : null}
          </div>
        </div>
      ) : null}
      <ConnectionSettingsPanel
        baseUrl={baseUrl}
        onBaseUrl={setBaseUrl}
        apiKey={apiKey}
        onApiKey={setApiKey}
        hasApiKey={hasApiKey}
        selectedVersion={selectedVersion}
        onVersion={(v) => {
          void handleVersionChange(v);
        }}
        autoDetected={autoDetected}
        switching={switchingVersion}
        switchNote={versionNote}
        testing={testing}
        result={result}
        onTest={() => {
          void testConnection();
        }}
      />

      <ImportWebhookSection
        webhookUrl={webhookUrl}
        onWebhookUrlChange={setWebhookUrl}
        copied={copied}
        onCopy={() => {
          void copyWebhookUrl();
        }}
        registering={registering}
        registerMsg={registerMsg}
        onRegister={() => {
          void registerWebhook();
        }}
        registered={registered}
        lastEventTicks={lastWebhookEventTicks}
      />

      <AddDefaultsSection
        rootFolderId={rootFolderId}
        onRootFolder={setRootFolderId}
        qualityProfileId={qualityProfileId}
        onQualityProfile={setQualityProfileId}
        rootFolderOptions={rootFolderOptions}
        qualityProfileOptions={qualityProfileOptions}
        listsLoaded={listsLoaded}
        tags={tags}
        onTags={setTags}
        monitorNew={monitorNew}
        onMonitorNew={setMonitorNew}
        allowUpgrades={allowUpgrades}
        onAllowUpgrades={setAllowUpgrades}
        upgradesSupported={selectedVersion !== "v2"}
      />

      <ReconciliationSection />
      <ImportLogSection />

      {dirty ? (
        <div className="pointer-events-none fixed inset-x-0 bottom-0 z-50 flex justify-center px-4 py-4">
          <div
            className="pointer-events-auto flex w-full max-w-3xl items-center gap-4 rounded-2xl border border-border bg-card px-5 shadow-lg"
            style={{ paddingTop: "0.875rem", paddingBottom: "0.875rem" }}
          >
            {/* The dot's fill rides on the host's colour custom properties rather than a bg-* utility:
                which of those the host emits depends on what Cove's own source writes, so one can
                resolve on a newer host and not on the floor this extension declares. */}
            <span
              className="h-2 w-2 shrink-0 rounded-full"
              style={{
                backgroundColor: saveError
                  ? "var(--color-red-400)"
                  : savedFlash
                    ? "var(--color-green-400)"
                    : "var(--color-amber-400)",
              }}
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
