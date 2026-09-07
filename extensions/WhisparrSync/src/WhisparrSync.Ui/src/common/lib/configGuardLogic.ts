/**
 * Pure, DOM-free wording for the configuration-completeness guard. Import-free (no React, no DOM, no SDK, not
 * even a type import) so the offline gate compiles it standalone, exactly like identityGuardLogic.ts.
 */

/** The settings label the UI actually shows for each guarded option key. */
const OPTION_LABELS: Record<string, string> = {
  baseUrl: "Whisparr URL",
  apiKey: "API key",
};

/** The settings section heading each guarded option lives under. */
const OPTION_SECTIONS: Record<string, string> = {
  baseUrl: "Connection",
  apiKey: "Connection",
};

const OPTION_SENTENCES: Record<string, string> = {
  baseUrl:
    "Set the Whisparr URL in Whisparr Sync settings (Connection) before acting — without it, every Whisparr action fails as though Whisparr were not running.",
  apiKey:
    "Set the API key in Whisparr Sync settings (Connection) before acting — without it, Whisparr rejects every request.",
};

/** Covers an unknown key, so no rendered sentence can carry a gap where a setting name should be. */
const FALLBACK_SENTENCE =
  "Finish setting up Whisparr Sync in its settings before acting — a required setting is not usable yet.";

/** The per-control counterpart, for the same reason: an unknown key must not leave a control naming nothing. */
const SHORT_FALLBACK_REASON = "Needs a Whisparr Sync setting that isn't usable yet";

/**
 * The visible settings label for an option key. An unrecognised key falls back to a neutral phrase rather than
 * to the key itself, so no sentence can leak a wire name.
 */
export function configOptionLabel(key: string): string {
  return OPTION_LABELS[key] ?? "a required setting";
}

/**
 * The refusal sentence for a set of unmet option keys. Several keys yield ONE sentence naming every one of them
 * with its section — never a composite "one or more settings" phrasing, since the server knows exactly which
 * are unmet and withholding that is the defect this guard exists to remove.
 */
export function configGuardMessage(keys: readonly string[]): string {
  const known = keys.filter((key) => key in OPTION_SENTENCES);
  if (known.length === 0) {
    return FALLBACK_SENTENCE;
  }
  if (known.length === 1) {
    return OPTION_SENTENCES[known[0]];
  }

  const named = known.map((key) => `the ${configOptionLabel(key)} (${OPTION_SECTIONS[key]})`);
  const list = `${named.slice(0, -1).join(", ")} and ${named[named.length - 1]}`;
  return `Whisparr Sync needs ${list} set in settings before acting — until each one is set, this action either fails outright or acquires nothing.`;
}

/**
 * The short counterpart to {@link configGuardMessage}, for one control rather than for a surface.
 *
 * The full sentence is a property of the connection, so it is stated once on each surface that carries guarded
 * controls; repeating all of it at every control is what buries the surface it sits on. This names the setting and
 * its section and stops there — the consequence clause is one statement up, on the same screen — so a control the
 * guard dims still says what it needs on hover and in its accessible name. Both wordings read {@link OPTION_LABELS}
 * and {@link OPTION_SECTIONS}, so the setting they name cannot drift apart.
 */
export function configShortReason(keys: readonly string[]): string {
  const known = keys.filter((key) => key in OPTION_SECTIONS);
  if (known.length === 0) {
    return SHORT_FALLBACK_REASON;
  }
  if (known.length === 1) {
    return `Needs the ${configOptionLabel(known[0])} setting (${OPTION_SECTIONS[known[0]]})`;
  }

  const named = known.map((key) => `${configOptionLabel(key)} (${OPTION_SECTIONS[key]})`);
  const list = `${named.slice(0, -1).join(", ")} and ${named[named.length - 1]}`;
  return `Needs the ${list} settings`;
}

/**
 * The setting-naming sentence for a configuration refusal, built from the option keys the SERVER put in the
 * refusal body rather than from a client-side snapshot — a control whose pre-click fact was stale is exactly the
 * case that reaches here, so the response is the only trustworthy source. This function only parses; anything
 * unparseable yields {@link configGuardMessage}'s own fallback, never a raw body.
 */
export function configIncompleteCopyFrom(body: string | null): string {
  let keys: readonly string[] = [];
  if (body) {
    try {
      const parsed = JSON.parse(body) as { options?: unknown };
      if (Array.isArray(parsed.options)) {
        keys = parsed.options.filter((key): key is string => typeof key === "string");
      }
    } catch {
      keys = [];
    }
  }
  return configGuardMessage(keys);
}
