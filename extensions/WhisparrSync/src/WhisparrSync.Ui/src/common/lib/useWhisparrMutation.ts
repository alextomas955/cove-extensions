/**
 * The busy/error/refresh triad shared by every WhisparrSync mutating surface: it flags the acting control,
 * awaits the mutation (plus any post-success refetch), and maps an {@link ApiError} through
 * {@link ./actionFailureLogic} — the guarantee being that a raw status/body never reaches rendered text. The
 * `pending` tag is caller-chosen: a multi-button surface tags each run to spinner one control; a single-action
 * surface takes the default `true` tag and reads `busy`.
 */
import { useCallback, useState } from "react";
import { ApiError } from "./coveApi";
import { actionFailureCopy } from "./actionFailureLogic";
import { configIncompleteCopyFrom } from "./configGuardLogic";

/** One mutation run: the acting tag, the failure verb phrase, the version-capability copy, and the work. */
interface WhisparrMutationRun<Tag> {
  /** Marks `pending` while the run is in flight; drives the acting control's own spinner. */
  tag: Tag;
  /** The verb phrase for the failure line ("Couldn't {label} — …"). */
  label: string;
  /** Returned verbatim by actionFailureLogic for a `versionUnsupported` failure (kept a param, not an import). */
  versionCapabilityCopy: string;
  /** The mutating call and any post-success refetch — everything the try must cover. */
  action: () => Promise<void>;
}

export interface WhisparrMutation<Tag> {
  /** The in-flight run's tag, or null when idle. */
  pending: Tag | null;
  /** `pending !== null`, for surfaces that only need a boolean disable. */
  busy: boolean;
  /** The friendly failure line from the last failed run, cleared at the next run's start. */
  error: string | null;
  clearError: () => void;
  run: (params: WhisparrMutationRun<Tag>) => Promise<void>;
}

/**
 * The one composition in the bundle that turns a caught mutation error into a rendered line
 * (`Couldn't {label} — {classified reason}`). The caught value is narrowed before any property is read off it,
 * which is what keeps a raw status or body out of rendered text: a value that is not an {@link ApiError} carries
 * neither, and classifies as unknown.
 *
 * Exported because the discovery Missing tab composes the same line outside this hook — its mutations ride a
 * shared store's optimistic-flip spine, not a per-control `run`, and a second composition there would be the one
 * place a raw body could reach the screen unchecked.
 */
export function mutationFailureLine(
  label: string,
  versionCapabilityCopy: string,
  err: unknown,
): string {
  const status = err instanceof ApiError ? err.status : -1;
  const body = err instanceof ApiError ? err.body : null;
  return actionFailureCopy(
    label,
    status,
    body,
    versionCapabilityCopy,
    configIncompleteCopyFrom(body),
  );
}

export function useWhisparrMutation<Tag = true>(): WhisparrMutation<Tag> {
  const [pending, setPending] = useState<Tag | null>(null);
  const [error, setError] = useState<string | null>(null);

  const run = useCallback(
    async ({ tag, label, versionCapabilityCopy, action }: WhisparrMutationRun<Tag>) => {
      setPending(tag);
      setError(null);
      try {
        await action();
      } catch (err) {
        setError(mutationFailureLine(label, versionCapabilityCopy, err));
      } finally {
        setPending(null);
      }
    },
    [],
  );

  const clearError = useCallback(() => {
    setError(null);
  }, []);

  return { pending, busy: pending !== null, error, clearError, run };
}
