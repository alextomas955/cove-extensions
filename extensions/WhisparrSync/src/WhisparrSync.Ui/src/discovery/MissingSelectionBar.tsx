/**
 * The Missing tab's selection-actions bar. The selection is hand-rolled over the string StashDB/TPDB sourceId
 * because the host selection bar keys on the integer Cove videoId and cannot host an external id.
 *
 * The bulk Monitor is additionally disabled when a required Whisparr Sync setting is unusable — it is the one
 * control here that CREATES a Whisparr record; Unmonitor flips and Search grabs an existing one, so both keep
 * working. The control carries the short requirement; where the setting is what actually dimmed it, the bar states
 * the full consequence sentence once for the whole tab, because that fact belongs to the CONNECTION and not to any
 * card, verb or selection. It is the reason the bar also draws with nothing selected: the tab's cards are dimmed
 * in exactly that state, and a statement that waited for a selection would be absent from the screen that needs it.
 *
 * Monitor / Unmonitor / Search are all per-scene v3-only, disabled on v2 with the shipped capability copy: the
 * per-scene discovery Search needs a scene-level Whisparr row to command, while the monitored-library search verb
 * stays available on either generation. Each gated control's reason rides its ENABLED wrapping span, because a
 * disabled button swallows hover. All text is a React text node (no raw-HTML injection) and every class is a
 * host-emitted utility copied from Cove.
 *
 * A bulk verb the server REFUSED states its own line here, beneath the verbs. Only a job that was enqueued rides
 * the job drawer; a refusal enqueued none, and it reaches no host alert either — the host's error alert belongs to
 * its action-handler dispatch, and these verbs are React click handlers inside an extension tab. Mark-all carries
 * no selection at all, which is why the bar also draws for a refusal alone: the line is the whole bar then, with
 * no count and no verbs, since there is no selection to state or act on. The two lines can be present together
 * and neither replaces the other — one is a pre-click fact about the connection, the other a verb the server has
 * just refused.
 */
import { Bookmark, BookmarkMinus, Loader2, Search } from "lucide-react";
import { StatusText } from "@cove-extensions/ui-shared";
import { configGuardMessage, configShortReason } from "../common/lib/configGuardLogic";
import { useConfigHealth } from "../common/lib/configHealthStore";
import { guardedControl } from "../common/lib/refusalAffordanceLogic";

// The bar's own host-emitted container, named once because both of its shapes wear it: the selection bar proper,
// and the refusal-only row a mark-all refusal draws with no selection behind it.
const BAR_CLASS =
  "flex flex-wrap items-center gap-3 bg-card/80 border border-border rounded-lg px-3 py-1.5 mx-1 mt-1";

// A status child of this wrapping ROW lands BESIDE the verbs unless it takes the full basis, and reads as a run-on
// against them at any width that fits. The full basis gives each statement a row beneath the controls it is about,
// which is where the placement rule wants it.
const BAR_STATUS_CLASS = "w-full";

export function MissingSelectionBar({
  selectedCount,
  monitoring,
  unmonitoring,
  searching,
  versionSupported,
  versionDisabledTitle,
  refusalReason = null,
  onMonitor,
  onUnmonitor,
  onSearch,
  onSelectAll,
  onInvert,
  onClear,
}: {
  selectedCount: number;
  monitoring: boolean;
  unmonitoring: boolean;
  searching: boolean;
  versionSupported: boolean;
  versionDisabledTitle: string;
  // The composed line for the last bulk verb the server refused, derived by the tab from the caught error. Null
  // until a bulk click failed, and cleared by the next bulk click or by Refresh.
  refusalReason?: string | null;
  onMonitor: () => void;
  onUnmonitor: () => void;
  onSearch: () => void;
  onSelectAll: () => void;
  onInvert: () => void;
  onClear: () => void;
}) {
  const config = useConfigHealth();
  // Gated on !loading so an in-flight read never disables; a failed read reports nothing unmet.
  const configIncomplete = !config.loading && config.missingRequiredOptions.length > 0;
  const configRequirement = configIncomplete
    ? configShortReason(config.missingRequiredOptions)
    : null;

  const monitorAffordance = guardedControl({
    name: "Monitor",
    capabilityReason: versionSupported ? null : versionDisabledTitle,
    configurationReason: configRequirement,
    busy: monitoring,
  });

  // Withheld where a stronger guard won: on a generation with no scene-level row every control this line could be
  // about is refused for a reason no setting can change, so the advice would change nothing here — and the reason
  // that DID win is stated once over the set instead. The setting stays described at the control that fixes it.
  const configSentence =
    monitorAffordance.cause === "configuration"
      ? configGuardMessage(config.missingRequiredOptions)
      : null;

  if (selectedCount === 0) {
    if (configSentence === null && refusalReason === null) {
      return null;
    }
    return (
      <div className={BAR_CLASS}>
        {configSentence !== null && (
          <div role="status" className={BAR_STATUS_CLASS}>
            <StatusText kind="warning">{configSentence}</StatusText>
          </div>
        )}
        {refusalReason !== null && (
          <div role="status" className={BAR_STATUS_CLASS}>
            <StatusText kind="warning">{refusalReason}</StatusText>
          </div>
        )}
      </div>
    );
  }

  return (
    <div className={BAR_CLASS}>
      <span className="text-xs text-secondary">{selectedCount} selected</span>
      <button type="button" onClick={onSelectAll} className="text-xs text-accent hover:underline">
        Select all
      </button>
      <button
        type="button"
        onClick={onInvert}
        className="text-xs text-secondary hover:text-foreground"
      >
        Invert
      </button>
      <button
        type="button"
        onClick={onClear}
        className="text-xs text-secondary hover:text-foreground"
      >
        Deselect all
      </button>
      <span
        title={monitorAffordance.title}
        className={monitorAffordance.reason === null ? undefined : "cursor-not-allowed"}
      >
        <button
          type="button"
          onClick={onMonitor}
          disabled={monitorAffordance.disabled}
          aria-label={monitorAffordance.ariaLabel}
          title={monitorAffordance.title}
          className="flex items-center gap-1 rounded px-2 py-0.5 text-xs text-accent hover:bg-accent/10 hover:text-accent-hover disabled:opacity-60"
        >
          {monitoring ? (
            <Loader2 className="h-3 w-3 animate-spin" />
          ) : (
            <Bookmark className="h-3 w-3" />
          )}
          Monitor
        </button>
      </span>
      <span
        title={versionSupported ? undefined : versionDisabledTitle}
        className={versionSupported ? undefined : "cursor-not-allowed"}
      >
        <button
          type="button"
          onClick={onUnmonitor}
          disabled={unmonitoring || !versionSupported}
          className="flex items-center gap-1 rounded px-2 py-0.5 text-xs text-secondary hover:text-foreground disabled:opacity-60"
        >
          {unmonitoring ? (
            <Loader2 className="h-3 w-3 animate-spin" />
          ) : (
            <BookmarkMinus className="h-3 w-3" />
          )}
          Unmonitor
        </button>
      </span>
      <span
        title={versionSupported ? undefined : versionDisabledTitle}
        className={versionSupported ? undefined : "cursor-not-allowed"}
      >
        <button
          type="button"
          onClick={onSearch}
          disabled={searching || !versionSupported}
          className="flex items-center gap-1 rounded px-2 py-0.5 text-xs text-green-400 hover:bg-green-900/20 hover:text-green-300 disabled:opacity-60"
        >
          {searching ? (
            <Loader2 className="h-3 w-3 animate-spin" />
          ) : (
            <Search className="h-3 w-3" />
          )}
          Search
        </button>
      </span>
      {configSentence !== null && (
        <div role="status" className={BAR_STATUS_CLASS}>
          <StatusText kind="warning">{configSentence}</StatusText>
        </div>
      )}
      {refusalReason !== null && (
        <div role="status" className={BAR_STATUS_CLASS}>
          <StatusText kind="warning">{refusalReason}</StatusText>
        </div>
      )}
    </div>
  );
}
