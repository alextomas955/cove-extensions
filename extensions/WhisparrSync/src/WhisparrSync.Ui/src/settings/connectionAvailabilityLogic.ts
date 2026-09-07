/**
 * Pure copy for the settings sections whose data comes from a live Whisparr read (the file settings). Zero
 * imports — the offline node:test gate compiles this module standalone.
 *
 * The distinction the copy encodes: a first-run "never connected" state and a saved-but-unreachable
 * one are different, and only the latter points at Test connection to retry. SettingsPage sets
 * `unreachable` only when a saved connection's live read fails, never on first run.
 */

/** The muted affordance a live-data section shows when its data isn't loaded, keyed on reachability. */
export function notLoadedMessage(unreachable: boolean, subject: string): string {
  return unreachable
    ? "Whisparr isn't reachable right now — Test connection above to retry."
    : `Test the connection to load ${subject}.`;
}
