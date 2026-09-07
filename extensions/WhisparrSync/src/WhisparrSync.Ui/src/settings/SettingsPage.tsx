/**
 * SettingsPage — the component the host mounts inside the "Whisparr Sync" SETTINGS TAB. It owns the shared
 * connection + add-defaults form state and the single floating save bar, and stacks the sections in a fixed
 * order: Connection · Import webhook · Add defaults · Whisparr file settings · Sync my library.
 *
 * A SINGLE save spans Connection + Library-path + Add-defaults on purpose: the server's SaveOptions rebuilds
 * the saved per-version connection on every write, so a per-section partial save would lose a field. One save
 * posting every field is the only safe shape. The API key is never modeled beyond `hasApiKey`; a blank key field
 * preserves the stored key server-side. There is no root-folder and no quality-profile setting — both are
 * derived per-add server-side from Whisparr's own lists.
 *
 * The host passes `{ onNavigate }`; this UI does not navigate, so it ignores it. Styling uses host Tailwind
 * token classes only (no hex, no CSS bundle).
 */
import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, X } from "lucide-react";
import { ApiError, request } from "../common/lib/coveApi";
import { Button, Spinner, StatusText } from "@cove-extensions/ui-shared";
import { ConnectionSettingsPanel } from "./ConnectionSettingsPanel";
import { ImportWebhookSection } from "./ImportWebhookSection";
import { AddDefaultsSection } from "./AddDefaultsSection";
import { WhisparrFileSettingsSection } from "./WhisparrFileSettingsSection";
import { SyncLibrarySection } from "./SyncLibrarySection";
import {
  fileSettingsFromServer,
  fileSettingsWriteBody,
  sameFileSettings,
  type FileSettings,
} from "./fileSettingsLogic";
import {
  DEFAULT_OPTIONS,
  optionsFromServer,
  type ConnectionView,
  type WhisparrOptions,
} from "./options";
import {
  readingForAddress,
  selectorForDetected,
  type ConnectionResult,
  type WhisparrVersion,
} from "./connectionResult";
import {
  NO_SYNC_PROBLEMS,
  hasSyncProblem,
  syncHealthFromServer,
  syncProblemSummary,
  type SyncHealth,
} from "./syncHealthLogic";
import {
  doubledPathDisplay,
  folderOverlapFromServer,
  hasFolderOverlapAdvisory,
  notApplicableSummary,
  notCheckedSummary,
  readFailedAnswer,
  rootContainmentSummary,
  sceneFolderOverlapSummary,
  type FolderOverlapAnswer,
} from "./folderOverlapLogic";
import { UNKNOWN_STATE_VISUAL } from "../common/lib/sceneStateVisual";
import { SceneStateGlyph } from "../common/ui/SceneStateGlyph";
import { PipelineHealthLines } from "./PipelineHealthLines";
import {
  connectionTimes,
  failingDependencies,
  hasFailingDependency,
  hasRecoveredDependency,
  pipelineHealthFromServer,
  recoveredDependencies,
  type DependencyHealthView,
} from "./pipelineHealthLogic";
import {
  registrationForVersion,
  webhookUrlFromServer,
  type WebhookRegistrationState,
  type WebhookUrlView,
} from "./webhookLogic";
import { clearConnectionScopedCaches } from "../common/lib/cacheRegistry";
import { actionFailureCopy, classifyActionFailure } from "../common/lib/actionFailureLogic";
import { configIncompleteCopyFrom } from "../common/lib/configGuardLogic";
import { VERSION_CAPABILITY_COPY } from "../common/lib/whisparrCopy";
import { api } from "../common/lib/extension";

interface TestConnectionResponse {
  result: ConnectionResult["kind"];
  version?: string | null;
  instanceName?: string | null;
  reason?: string | null;
  detected?: string | null;
}

/**
 * The liveness probe for a connection the page holds but has not tested this session: POSTs the creds (in the
 * body, never a query string) at the connect route and THROWS when the instance cannot answer. Its answer is
 * discarded — what the page needs from it is whether the call succeeded at all.
 */
async function probeConnection(baseUrl: string, apiKey: string): Promise<void> {
  await request(api("test-connection"), {
    method: "POST",
    body: JSON.stringify({ baseUrl, apiKey }),
  });
}

/**
 * Read the webhook view from the connector: Whisparr's own "Cove Whisparr Sync" connection is the source of
 * truth for both the URL and the registered flag, so a refresh reflects the live connection rather than a
 * browser-derived localhost. The server degrades to the derived default + registered:false when no connection
 * exists (or Whisparr is down).
 */
async function fetchWebhookUrl(): Promise<WebhookUrlView> {
  return webhookUrlFromServer(await request(api("webhook-url")));
}

/** The most recent webhook event ticks + the sync-health signal, from one `/import-log` read. */
async function fetchImportStatus(): Promise<{
  lastTicks: number | null;
  syncHealth: SyncHealth;
  pipelineHealth: DependencyHealthView[];
}> {
  try {
    const resp = await request<{
      lastEventTicks?: unknown;
      syncHealth?: unknown;
      pipelineHealth?: unknown;
    }>(api("import-log"));
    // The server supplies the last webhook-event tick pre-reduced (0 = none received) rather than an
    // entries array — the UI no longer computes max(utcTicks) itself.
    const lastTicks =
      typeof resp.lastEventTicks === "number" && resp.lastEventTicks > 0
        ? resp.lastEventTicks
        : null;
    return {
      lastTicks,
      syncHealth: syncHealthFromServer(resp.syncHealth),
      pipelineHealth: pipelineHealthFromServer(resp.pipelineHealth),
    };
  } catch {
    // A failed log read is not a settings error.
    return { lastTicks: null, syncHealth: NO_SYNC_PROBLEMS, pipelineHealth: [] };
  }
}

/**
 * The folder-overlap answer. A thrown read yields the NOT-CHECKED answer, not an empty finding list: returning
 * `[]` here is what used to turn an outage into a silent all-clear, and it is also why the route answers 200 with
 * `checked:false` rather than a 400 this catch would swallow.
 */
async function fetchFolderOverlap(): Promise<FolderOverlapAnswer> {
  try {
    return folderOverlapFromServer(await request(api("folder-overlap")));
  } catch {
    return readFailedAnswer();
  }
}

// sessionStorage (not localStorage): a dismissed advisory should reappear next session if the root is still
// misconfigured, so the guidance is not lost forever behind one click.
const OVERLAP_DISMISSED_KEY = "whisparr:folder-overlap-dismissed";

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
  const [connected, setConnected] = useState(false);
  // A saved connection whose live read failed (Whisparr down at load, or a failed test) — distinct
  // from a first-run never-connected state, which the not-loaded copy must word differently.
  const [connectionUnreachable, setConnectionUnreachable] = useState(false);

  const [saved, setSaved] = useState<WhisparrOptions>(DEFAULT_OPTIONS);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saveRetryable, setSaveRetryable] = useState(true);
  const [savedFlash, setSavedFlash] = useState(false);

  const [webhookUrl, setWebhookUrl] = useState("");
  const [copied, setCopied] = useState(false);
  const [registering, setRegistering] = useState(false);
  const [registerMsg, setRegisterMsg] = useState<string | null>(null);
  // The SELECTED version's own answer. Starts unchecked: before a read there is no instance-specific evidence,
  // and reporting the previous version's flag is the defect this state removes.
  const [registered, setRegistered] = useState<WebhookRegistrationState>("notChecked");
  const [lastWebhookEventTicks, setLastWebhookEventTicks] = useState<number | null>(null);
  const [syncHealth, setSyncHealth] = useState<SyncHealth>(NO_SYNC_PROBLEMS);
  const [pipelineHealth, setPipelineHealth] = useState<DependencyHealthView[]>([]);
  // null = not read yet, which is deliberately NOT the same as "not checked": the advisory stays absent until an
  // answer arrives rather than flashing a couldn't-compare line on first paint.
  const [folderOverlap, setFolderOverlap] = useState<FolderOverlapAnswer | null>(null);
  const [overlapDismissed, setOverlapDismissed] = useState(() => {
    try {
      return sessionStorage.getItem(OVERLAP_DISMISSED_KEY) === "1";
    } catch {
      return false;
    }
  });

  // Whisparr's own file-affecting toggles (v3 only). null = not loaded (no connection) — the section shows a
  // "Test the connection" affordance rather than a guessed all-off state. Folded into the page dirty check +
  // Save bar, so a write goes through the configure-gated /file-settings read-modify-write with the parent.
  const [fileSettings, setFileSettings] = useState<FileSettings | null>(null);
  const [savedFileSettings, setSavedFileSettings] = useState<FileSettings | null>(null);

  // Read Whisparr's four file-affecting toggles (v3 only) for the file-settings section. v2's config fields
  // are Sonarr-shaped and diverge, so the editor is v3-only — on v2 the section shows a version note, never a
  // guessed state. A failed/absent read leaves both at null (the not-loaded affordance). Stable identity
  // (empty deps; the setters + `request` are stable) so it can sit in the mount effect's dep list.
  const loadFileSettings = useCallback(async (version: WhisparrVersion) => {
    if (version === "v2") {
      setFileSettings(null);
      setSavedFileSettings(null);
      return;
    }
    try {
      const s = fileSettingsFromServer(await request(api("file-settings")));
      setFileSettings(s);
      setSavedFileSettings(s);
    } catch {
      setFileSettings(null);
      setSavedFileSettings(null);
    }
  }, []);

  // Load the persisted options once on mount; if a key is already stored, populate the dropdowns + webhook from
  // the live API using the stored creds (an empty submitted key falls back to the stored one server-side).
  useEffect(() => {
    void (async () => {
      try {
        const opts = optionsFromServer(await request(api("options")));
        setBaseUrl(opts.BaseUrl);
        setSelectedVersion(opts.SelectedVersion);
        setTags(opts.TagsOnAdd);
        setMonitorNew(opts.MonitorNewByDefault);
        setAllowUpgrades(opts.AllowQualityUpgrades);
        setHasApiKey(opts.hasApiKey);
        setSavedConnections(opts.SavedConnections);
        setSaved(opts);
        if (opts.hasApiKey && opts.BaseUrl) {
          // The webhook read is connector-sourced but degrades server-side to a derived default, so it loads
          // independently of the live probe below; a thrown read is non-fatal and leaves the first-run copy.
          try {
            const wh = await fetchWebhookUrl();
            setWebhookUrl(wh.url);
            // Authoritative from that instance's own connector, not session-inferred.
            setRegistered(registrationForVersion(wh, opts.SelectedVersion));
          } catch {
            // Non-fatal: leave the first-run copy.
          }
          const status = await fetchImportStatus();
          setLastWebhookEventTicks(status.lastTicks);
          setSyncHealth(status.syncHealth);
          setPipelineHealth(status.pipelineHealth);
          setFolderOverlap(await fetchFolderOverlap());
          // The live-Whisparr probe: a failure here is what "unreachable" means for a saved connection.
          try {
            await probeConnection(opts.BaseUrl, "");
            setConnected(true);
            await loadFileSettings(opts.SelectedVersion);
          } catch {
            setConnectionUnreachable(true);
          }
        }
      } catch {
        // Couldn't even read the stored options: treat as first run; the dropdowns stay in their empty state.
      }
    })();
  }, [loadFileSettings]);

  async function testConnection() {
    setTesting(true);
    setResult(null);
    setVersionNote(null);
    try {
      const resp = await request<TestConnectionResponse>(api("test-connection"), {
        method: "POST",
        body: JSON.stringify({ baseUrl, apiKey }),
      });
      switch (resp.result) {
        case "success": {
          setConnectionUnreachable(false);
          setResult({
            kind: "success",
            url: baseUrl,
            instanceName: resp.instanceName ?? "Whisparr",
            version: resp.version ?? "unknown",
          });
          const detected = selectorForDetected(resp.version);
          if (detected) {
            setSelectedVersion(detected);
            setAutoDetected(true);
          }
          setConnected(true);
          // Read the live sections from the just-tested creds (not yet saved — the key is in memory only).
          try {
            const wh = await fetchWebhookUrl();
            setWebhookUrl(wh.url);
            setRegistered(registrationForVersion(wh, detected ?? selectedVersion));
            // The probe has just stamped the version and its tick server-side (only for the stored host), so
            // read them back rather than assuming this test wrote them.
            const stamped = optionsFromServer(await request(api("options")));
            setSaved(stamped);
            await loadFileSettings(detected ?? selectedVersion);
          } catch {
            setConnected(false);
          }
          break;
        }
        case "badKey":
          setResult({ kind: "badKey", url: baseUrl });
          break;
        case "notWhisparr":
          setResult({ kind: "notWhisparr", url: baseUrl });
          break;
        case "versionMismatch":
          setResult({
            kind: "versionMismatch",
            url: baseUrl,
            detected: resp.detected ?? "an unknown version",
          });
          setAutoDetected(false);
          break;
        default:
          setConnectionUnreachable(true);
          setResult({ kind: "unreachable", url: baseUrl });
          break;
      }
    } catch {
      // A thrown request is the unreachable class; the backend returns a 200 discriminator for every
      // reachable-Whisparr outcome (bad key, HTML, etc.).
      setConnectionUnreachable(true);
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
      // One save posting EVERY field: the server rebuilds the saved per-version connection on write, so a
      // partial body would lose one. An empty ApiKey preserves the stored key (write-only).
      // The server echoes the redaction-safe options back — including the recomputed per-version
      // SavedConnections — so snapshot from the response rather than reconstructing it here.
      const body: Record<string, unknown> = {
        BaseUrl: baseUrl,
        ApiKey: apiKey,
        SelectedVersion: selectedVersion,
        TagsOnAdd: tags,
        MonitorNewByDefault: monitorNew,
        AllowQualityUpgrades: allowUpgrades,
      };
      const snapshot = optionsFromServer(
        await request(api("options"), {
          method: "POST",
          body: JSON.stringify(body),
        }),
      );
      setSaved(snapshot);
      setSavedConnections(snapshot.SavedConnections);
      setHasApiKey(snapshot.hasApiKey);
      setApiKey(""); // never keep the key in the field after a save
      // Persist any file-settings change through the SAME save: the server does the read-modify-write from
      // exactly the four whitelisted PascalCase booleans (never a whole config object), so unknown Whisparr
      // config fields survive. Skipped on a version switch — the reload below reloads that version's config.
      if (
        !versionChanged &&
        selectedVersion !== "v2" &&
        fileSettings !== null &&
        savedFileSettings !== null &&
        !sameFileSettings(fileSettings, savedFileSettings)
      ) {
        await request(api("file-settings"), {
          method: "POST",
          body: JSON.stringify(fileSettingsWriteBody(fileSettings)),
        });
        setSavedFileSettings(fileSettings);
      }
      // A saved connection change (version/URL/key) makes the slot components' cached Whisparr status stale;
      // drop those module-level caches so the next studio/performer/scene view refetches against the new config.
      clearConnectionScopedCaches();
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
      const status = err instanceof ApiError ? err.status : -1;
      const body = err instanceof ApiError ? err.body : null;
      // No arm reads a message off the caught value. That read is how the SDK's transport string — an
      // internal route path plus an untranslated HTTP status — reached the save bar; a fixed fallback is
      // free here because a non-ApiError classifies as `unknown`, whose sentence is already written.
      setSaveError(
        actionFailureCopy(
          "save",
          status,
          body,
          VERSION_CAPABILITY_COPY,
          configIncompleteCopyFrom(body),
        ),
      );
      // A configuration refusal will refuse an immediate retry identically, so it is the one class that
      // must not be offered one.
      setSaveRetryable(classifyActionFailure(status, body) !== "configIncomplete");
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
      // reach; the server keeps only its origin and re-mints the token from the stored secret. The backend is
      // idempotent (update-or-create; 409/unique-name is success), so re-registering an existing connection
      // returns registered:true and never surfaces an error — only a thrown request (Whisparr unreachable) does.
      const resp = await request<{ registered: boolean }>(api("register-webhook"), {
        method: "POST",
        body: JSON.stringify({ Url: webhookUrl }),
      });
      setRegistered(resp.registered ? "registered" : "notRegistered");
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

  const fileSettingsDirty =
    selectedVersion !== "v2" &&
    fileSettings !== null &&
    savedFileSettings !== null &&
    !sameFileSettings(fileSettings, savedFileSettings);

  const dirty =
    apiKey.length > 0 ||
    baseUrl !== saved.BaseUrl ||
    selectedVersion !== saved.SelectedVersion ||
    !sameTags(tags, saved.TagsOnAdd) ||
    monitorNew !== saved.MonitorNewByDefault ||
    allowUpgrades !== saved.AllowQualityUpgrades ||
    fileSettingsDirty;

  // Each output tracks its own input; the derivation is where a later reader would be tempted to cross them.
  const times = connectionTimes(saved.DetectedVersionTicks, pipelineHealth);

  // The last test's answer, shown only while the field still holds the address it was taken against. An
  // edit retires the banner and the detection it carried — the same pair handleVersionChange clears.
  // A comparison, not a clear in the field's onChange: `result` still holds the reading, so the same true
  // sentence returns if the address does.
  const reading = readingForAddress(result, baseUrl);

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
        hasApiKey: hasApiKey || apiKey.length > 0,
      },
    };
    setSavedConnections(stash);
    setSelectedVersion(v);

    const target = v in stash ? stash[v] : undefined;
    setBaseUrl(target?.BaseUrl ?? "");
    setHasApiKey(target?.hasApiKey ?? false);
    setApiKey(""); // blank = keep that version's saved key on Save

    const label = v === "v2" ? "v2" : "v3 (Eros)";
    // The target version's file settings are a different instance's config; clear until its dropdowns repopulate.
    setFileSettings(null);
    setSavedFileSettings(null);
    // Same clear-until-repopulated rule for the registration answer, on EVERY branch below: the flag on screen
    // belongs to the instance just switched away from, and keeping it would assert a connector Cove has not
    // looked for on this one.
    setRegistered("notChecked");
    if (!target?.hasApiKey || !target.BaseUrl) {
      // Never configured this version: blank the form for a fresh setup and say so (a genuine first
      // run for this version, not an unreachable saved one).
      setConnected(false);
      setConnectionUnreachable(false);
      setSwitchingVersion(false);
      setVersionNote(
        `No saved ${label} connection yet — enter its URL and key, then Test connection.`,
      );
      return;
    }

    // Probe the target instance so its live sections repopulate. The server pairs the saved key with the
    // saved URL, so a blank key still authenticates against that version's own host.
    setSwitchingVersion(true);
    setVersionNote(null);
    try {
      await probeConnection(target.BaseUrl, "");
      setConnected(true);
      setConnectionUnreachable(false);
      await loadFileSettings(v);
      // The read answers for every saved connection, so the target instance's own registration is available
      // here — before the save that makes it active. A thrown read leaves the unchecked state as-is.
      try {
        const wh = await fetchWebhookUrl();
        setRegistered(registrationForVersion(wh, v));
      } catch {
        // Non-fatal: a failed webhook read is not a settings error, and unchecked is the honest answer.
      }
      setVersionNote(`Loaded your saved ${label} connection — Save to switch to it.`);
    } catch {
      // The saved instance is unreachable or its key was rejected: keep the restored URL, drop the live state.
      setConnected(false);
      setConnectionUnreachable(true);
      setVersionNote(
        `Loaded your saved ${label} connection, but it isn't reachable right now — Test connection to retry.`,
      );
    } finally {
      setSwitchingVersion(false);
    }
  }

  function dismissFolderOverlap() {
    try {
      sessionStorage.setItem(OVERLAP_DISMISSED_KEY, "1");
    } catch {
      // sessionStorage unavailable (private mode / disabled): still hide it for the rest of this render.
    }
    setOverlapDismissed(true);
  }

  return (
    <div className="space-y-6" style={{ paddingBottom: dirty ? "5rem" : undefined }}>
      {folderOverlap && hasFolderOverlapAdvisory(folderOverlap) && !overlapDismissed ? (
        <div
          role="alert"
          className="flex items-start gap-3 rounded-2xl border border-amber-500/40 bg-amber-500/10 px-4 py-3"
          // React escapes an attribute expression and builds no markup, and an empty list omits the attribute
          // rather than rendering a title="".
          title={
            folderOverlap.notApplicable.length > 0
              ? folderOverlap.notApplicable
                  .map((kind) => notApplicableSummary(kind, selectedVersion))
                  .join("\n")
              : undefined
          }
        >
          <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-400" />
          <div className="min-w-0 flex-1 space-y-2">
            <p className="text-sm font-semibold text-foreground">
              Check how your Cove and Whisparr folders line up
            </p>
            {folderOverlap.findings.map((f) =>
              f.kind === "rootContainment" ? (
                <div key={`rootContainment:${f.whisparrRoot}:${f.coveRoot}`} className="space-y-1">
                  <p className="truncate font-mono text-xs text-muted" title={f.whisparrRoot}>
                    Whisparr root: {f.whisparrRoot}
                  </p>
                  <p className="truncate font-mono text-xs text-muted" title={f.coveRoot}>
                    Cove root: {f.coveRoot}
                  </p>
                  <p className="text-sm text-secondary">{rootContainmentSummary(f)}</p>
                </div>
              ) : (
                <div key={`sceneFolderFormat:${f.root}`} className="space-y-1">
                  <p className="truncate font-mono text-xs text-muted" title={f.root}>
                    Root: {f.root}
                  </p>
                  <p
                    className="truncate font-mono text-xs text-muted"
                    title={doubledPathDisplay(f)}
                  >
                    Whisparr writes: {doubledPathDisplay(f)}
                  </p>
                  <p className="text-sm text-secondary">{sceneFolderOverlapSummary(f)}</p>
                </div>
              ),
            )}
            {/* The glyph, not the colour, is what tells "we could not check" apart from "nothing found". */}
            {!folderOverlap.checked && folderOverlap.reason ? (
              <div className="flex items-start gap-2">
                <SceneStateGlyph
                  iconKey={UNKNOWN_STATE_VISUAL.iconKey}
                  color={UNKNOWN_STATE_VISUAL.color}
                  filled={UNKNOWN_STATE_VISUAL.filled}
                  className="mt-0.5 h-4 w-4 shrink-0"
                />
                <p className="text-sm text-secondary">{notCheckedSummary(folderOverlap.reason)}</p>
              </div>
            ) : null}
          </div>
          <button
            type="button"
            onClick={dismissFolderOverlap}
            className="shrink-0 rounded-md p-1 text-muted hover:text-foreground"
            aria-label="Dismiss"
          >
            <X className="h-4 w-4" />
          </button>
        </div>
      ) : null}

      {hasSyncProblem(syncHealth) || hasFailingDependency(pipelineHealth) ? (
        <div
          role="alert"
          className="flex items-start gap-3 rounded-2xl border border-red-500/40 bg-red-500/10 px-4 py-3"
        >
          <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-red-400" />
          <div className="min-w-0 space-y-2">
            {/* The heading follows the finding: a block open only because a dependency is failing must not
                claim the sync problem it does not have. */}
            <p className="text-sm font-semibold text-foreground">
              {hasSyncProblem(syncHealth)
                ? "Sync problem — Cove can\u2019t find imported files"
                : "Whisparr Sync isn\u2019t working right now"}
            </p>
            {hasSyncProblem(syncHealth) ? (
              <div className="min-w-0 space-y-1">
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
            ) : null}
            {hasFailingDependency(pipelineHealth) ? (
              <PipelineHealthLines entries={failingDependencies(pipelineHealth)} failing />
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
        autoDetected={autoDetected && reading !== null}
        detectedVersion={saved.DetectedVersion}
        versionVerifiedTicks={times.versionVerifiedTicks}
        whisparrLastReachableTicks={times.whisparrLastReachableTicks}
        switching={switchingVersion}
        switchNote={versionNote}
        testing={testing}
        result={reading}
        // The raw result, not the address-scoped one. This prompt speaks for the SAVED connection's
        // load-time read, so its condition is "no test run this session"; the scoped value would let it
        // reappear after an edit retired a test that did run.
        unreachableOnLoad={connectionUnreachable && result === null}
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
        registration={registered}
        lastEventTicks={lastWebhookEventTicks}
      />

      <AddDefaultsSection
        tags={tags}
        onTags={setTags}
        monitorNew={monitorNew}
        onMonitorNew={setMonitorNew}
        allowUpgrades={allowUpgrades}
        onAllowUpgrades={setAllowUpgrades}
        upgradesSupported={selectedVersion !== "v2"}
      />

      <WhisparrFileSettingsSection
        settings={fileSettings}
        versionSupported={selectedVersion !== "v2"}
        unreachable={connectionUnreachable}
        onChange={setFileSettings}
      />

      <SyncLibrarySection
        connected={connected}
        isV2={selectedVersion === "v2"}
        unreachable={connectionUnreachable}
      />

      {/* A past error must never raise an alarm, so the recovered state is a muted, non-boxed status region —
          which is also what keeps the boxed-advisory count at three. It sits BELOW the setup run because it is
          neither blocking nor current, and this is a setup page: nothing that has already resolved itself may
          take the space in front of the first control. A dependency failing right now is the opposite case and
          its alert deliberately stays at the top. */}
      {!hasFailingDependency(pipelineHealth) && hasRecoveredDependency(pipelineHealth) ? (
        <div role="status" className="flex items-start gap-3 px-1">
          <div className="min-w-0 flex-1 space-y-1">
            <p className="text-sm font-semibold text-secondary">Recently recovered</p>
            <PipelineHealthLines entries={recoveredDependencies(pipelineHealth)} failing={false} />
          </div>
        </div>
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
                  {saveError}
                  {saveRetryable ? " Your changes are still here; try Save again." : null}
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
