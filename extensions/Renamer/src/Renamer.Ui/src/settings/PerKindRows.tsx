/**
 * PerKindRows - the "Per kind" list inside the "Where files go" card: one row per renamable kind,
 * reading whether it follows the card's default destination, has a folder of its own, or is not
 * renamed at all, with the two buttons that move it between those states.
 *
 * A kind with no stored entry follows the default, so the list reads an absent entry as renamed with
 * no folder of its own. Presentational; every edit flows up through the `set` callback the panel
 * threads in from useRenamerOptions.
 */
import type { ReactNode } from "react";

import { Button, StatusText } from "@cove-extensions/ui-shared";

import { DestinationField } from "./DestinationField";
import { kindSettings, kindsSummary, nextKinds } from "./entityKindsLogic";
import {
  KIND_LABELS,
  NO_DESTINATION,
  RENAMABLE_KINDS,
  type Destination,
  type LibraryPathsState,
  type RenamableKind,
  type RenamerOptions,
} from "./options";

export interface PerKindRowsProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
  library: LibraryPathsState;
}

export function PerKindRows({ options, set, library }: Readonly<PerKindRowsProps>) {
  const update = (kind: RenamableKind, enabled: boolean, destination: Destination | null) => {
    set("Kinds", nextKinds(options.Kinds, kind, enabled, destination));
  };

  const { excluded, ownFolder } = kindsSummary(options.Kinds);

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-baseline gap-3">
        <span className="text-sm font-semibold text-foreground">Per kind</span>
        <span className="text-xs text-secondary">
          {excluded === 0 && ownFolder === 0
            ? "Every kind follows the settings above. Nothing here needs changing."
            : `${excluded} excluded · ${ownFolder} with their own folder`}
        </span>
      </div>

      <div className="divide-y divide-border rounded-xl border border-border">
        {RENAMABLE_KINDS.map((kind) => {
          const { Enabled: enabled, Destination: destination } = kindSettings(options.Kinds, kind);
          const followsDefault = enabled && destination === null;

          let state: ReactNode;
          if (!enabled) {
            state = <StatusText kind="warning">Not renamed</StatusText>;
          } else if (destination === null) {
            state = <StatusText kind="muted">Follows the default</StatusText>;
          } else {
            state = (
              <DestinationField
                value={destination}
                onChange={(v) => {
                  update(kind, enabled, v);
                }}
                library={library}
                label={`${KIND_LABELS[kind]} folder template`}
              />
            );
          }

          return (
            // Each row names itself, so the four identical button pairs are told apart by a screen
            // reader and the row's own controls are reachable as a set.
            <div
              key={kind}
              role="group"
              aria-label={KIND_LABELS[kind]}
              className="flex flex-wrap items-center gap-4 px-4 py-3"
            >
              <span className="w-32 shrink-0 text-sm font-medium text-foreground">
                {KIND_LABELS[kind]}
              </span>
              <div className="min-w-0 flex-1">{state}</div>
              <div className="flex shrink-0 gap-2">
                <Button
                  variant="ghost"
                  onClick={() => {
                    update(kind, true, followsDefault ? { ...NO_DESTINATION } : null);
                  }}
                >
                  {followsDefault ? "Own folder" : "Use default"}
                </Button>
                {/* Excluding keeps whatever folder the kind had, so re-including restores it rather
                    than handing back a row the user has to set up again. */}
                <Button
                  variant="ghost"
                  onClick={() => {
                    update(kind, !enabled, destination);
                  }}
                >
                  {enabled ? "Exclude" : "Include"}
                </Button>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
