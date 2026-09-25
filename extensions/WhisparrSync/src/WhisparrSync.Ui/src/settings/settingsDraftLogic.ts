/**
 * Pure rules for the page's one settings draft: which of its fields differ from what is stored, the
 * sentence that names them, and the body one save sends.
 *
 * Relative imports only, so this module runs with no environment. The wire types arrive as
 * `import type`, which erases at runtime.
 */
import type {
  KeyWriteSignal,
  UpgradeBehavior,
  WhisparrSyncGenerationSaveRequest,
  WhisparrSyncSettingsSaveRequest,
  WhisparrSyncSettingsView,
} from "../wire/api";
import { isAddressEdit, valuesForCard, type CardGeneration } from "./connectLogic";

/** Everything the page's plain settings controls hold, and everything one save writes. */
export interface SettingsDraft {
  readonly generation: CardGeneration;
  readonly address: string;
  /** The key typed this session. Blank leaves the stored key alone. */
  readonly apiKey: string;
  /** The stored key is to be removed by the next save. */
  readonly keyCleared: boolean;
  /** Null until the settings read answers, which is not a choice anyone made. */
  readonly upgradeBehavior: UpgradeBehavior | null;
}

/** A draft member that differs from what is stored. */
export type UnsavedField = "generation" | "address" | "apiKey" | "upgradeBehavior";

/** In the order the page draws the controls that hold them. */
const FIELD_ORDER: readonly UnsavedField[] = ["generation", "address", "apiKey", "upgradeBehavior"];

const FIELD_NAMES: Record<UnsavedField, string> = {
  generation: "the Whisparr generation",
  address: "the Whisparr address",
  apiKey: "the API key",
  upgradeBehavior: "the replacement-file behaviour",
};

/** Said whenever the generation is unsaved, because saving it reloads the page. */
const RELOAD_SENTENCE = "Saving changes the generation Cove uses and reloads the page.";

/** The draft a generation's stored values seed. */
export function draftFor(
  view: WhisparrSyncSettingsView,
  generation: CardGeneration,
): SettingsDraft {
  return {
    generation,
    address: valuesForCard(view, generation)?.address ?? "",
    apiKey: "",
    keyCleared: false,
    upgradeBehavior: view.upgradeBehavior,
  };
}

/**
 * The members a save would write, or none at all while no settings have arrived. An address that
 * differs from the stored one only by case or a trailing separator is not one of them, as the
 * server's own same-address rule is not moved by either.
 */
export function unsavedFields(
  view: WhisparrSyncSettingsView | null,
  draft: SettingsDraft,
): readonly UnsavedField[] {
  if (view === null) {
    return [];
  }
  const stored = valuesForCard(view, draft.generation);
  const differs: Record<UnsavedField, boolean> = {
    generation: draft.generation !== view.selectedGeneration,
    address: isAddressEdit(stored?.address ?? "", draft.address),
    apiKey: draft.keyCleared || draft.apiKey !== "",
    upgradeBehavior:
      draft.upgradeBehavior !== null && draft.upgradeBehavior !== view.upgradeBehavior,
  };
  return FIELD_ORDER.filter((field) => differs[field]);
}

/** What the save bar says while `fields` are unsaved, and nothing at all while none are. */
export function unsavedSummary(fields: readonly UnsavedField[]): string {
  if (fields.length === 0) {
    return "";
  }
  const names = fields.map((field) => FIELD_NAMES[field]);
  const list =
    names.length === 1 ? names[0] : `${names.slice(0, -1).join(", ")} and ${names.at(-1)}`;
  const sentence = `${list.charAt(0).toUpperCase()}${list.slice(1)} ${
    names.length === 1 ? "is" : "are"
  } not saved yet.`;
  return fields.includes("generation") ? `${sentence} ${RELOAD_SENTENCE}` : sentence;
}

/**
 * The body one save sends, built from the unsaved members alone.
 *
 * The generation the draft does not name is always null, and the one it does is sent only when its
 * address or its key is unsaved, so a save of some other field writes neither stored connection. A
 * blank key field is a request to keep the stored key, and the key never travels on either of the
 * other two signals.
 */
export function writeRequestFor(
  view: WhisparrSyncSettingsView | null,
  draft: SettingsDraft,
): WhisparrSyncSettingsSaveRequest {
  const unsaved = unsavedFields(view, draft);
  const keyWrite = keyWriteFor(draft);
  const half: WhisparrSyncGenerationSaveRequest | null =
    unsaved.includes("address") || unsaved.includes("apiKey")
      ? { address: draft.address, keyWrite, apiKey: keyWrite === "replace" ? draft.apiKey : null }
      : null;

  return {
    selectedGeneration: draft.generation,
    v3: draft.generation === "v3" ? half : null,
    v2: draft.generation === "v2" ? half : null,
    upgradeBehavior: unsaved.includes("upgradeBehavior") ? draft.upgradeBehavior : null,
  };
}

/** The whole mapping: a marked-for-removal stored key clears, a blank field keeps, a typed key replaces. */
function keyWriteFor(draft: SettingsDraft): KeyWriteSignal {
  if (draft.keyCleared) {
    return "clear";
  }
  return draft.apiKey === "" ? "keep" : "replace";
}

/**
 * Whether pressing Test asks about the stored connection rather than about a typed pair.
 *
 * The key is write-only, so a page that has just saved one holds no copy to send back. Asking about
 * the stored connection is the only way a test can run in that state, and it is the only test whose
 * answer may update the recorded version. An unsaved replacement behaviour is not part of the
 * connection and does not change what a test asks about.
 */
export function testsStoredConnection(
  view: WhisparrSyncSettingsView | null,
  draft: SettingsDraft,
): boolean {
  const stored = valuesForCard(view, draft.generation);
  if (!stored?.keyIsSet) {
    return false;
  }
  const unsaved = unsavedFields(view, draft);
  return (
    !unsaved.includes("generation") && !unsaved.includes("address") && !unsaved.includes("apiKey")
  );
}
