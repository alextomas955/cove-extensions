/**
 * The three cross-slice Whisparr copy literals, single-sourced here to keep the monitor, scene, and entity
 * surfaces that all render them from drifting the wording between each other. Import-free (plain string
 * constants) so any slice depends downward on it.
 */

/**
 * The disabled-control copy shown when the connected Whisparr version does not offer an outward capability
 * for the selected entity (e.g. a performer on v2, whose series model has no performer resource, or a
 * per-scene action on v2). It states where the capability IS available and deliberately implies NO migration:
 * v2 and v3 are both first-class. The monitor button, the scene panel, and the library toolbar all read this
 * ONE literal so the wording cannot drift into "needs v3" messaging.
 */
export const VERSION_CAPABILITY_COPY = "Currently available on Whisparr v3 (Eros)";

/**
 * The wording for a connected BUILD that declares none of the routes an operation needs — a different fact
 * from {@link VERSION_CAPABILITY_COPY}, which is about the generation. It names the remedy that actually
 * applies (a newer build of the generation already connected) and states no time, because the absence is a
 * property of that build: nothing a user waits for changes it, so any "try again" phrasing here is advice
 * that cannot work.
 */
export const BUILD_CAPABILITY_COPY =
  "This Whisparr build doesn't offer the endpoints this needs. Updating Whisparr to a newer build adds them.";

/**
 * The single-sourced wording for the case the backend's one `error`/`summary.error` boolean collapses
 * together: Whisparr was never configured, or it's configured but currently unreachable. Deliberately
 * honest about both rather than asserting the former — every surface reading that boolean (the monitor
 * button, the status line, the library toolbar summary, the scene panel) imports this ONE literal so
 * the wording can't drift between them again.
 */
export const WHISPARR_UNAVAILABLE_COPY =
  "Whisparr isn't reachable. Check the connection in Settings.";

/**
 * The Search-outcome copy for a 200 whose `searched` boolean is false — the acquisition side simply holds no
 * entry for the scene. That is a third cause, distinct from the version-capability cause
 * ({@link VERSION_CAPABILITY_COPY}) and from the never-configured-or-unreachable cause
 * ({@link WHISPARR_UNAVAILABLE_COPY}), its two siblings on the same control; conflating any two of the three is
 * the drift this module exists to prevent. It names a remedy the user can perform (mark the scene wanted) and
 * implies no migration between Whisparr generations.
 */
export const SEARCH_NOT_ADDED_COPY =
  "Whisparr has no entry for this scene yet, so there is nothing to search for — mark it wanted first.";
