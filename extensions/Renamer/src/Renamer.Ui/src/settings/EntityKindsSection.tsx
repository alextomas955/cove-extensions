/**
 * EntityKindsSection - the "Entity kinds" card: per kind, whether this extension renames it at all
 * and, optionally, the folder its items go to when no routing rule matches them.
 *
 * A kind with no stored entry is renamed with the default folder template and root, so the card
 * reads an absent entry as on with no folder of its own. Presentational; every edit flows up through
 * the `set` callback the panel threads in from useRenamerOptions.
 */
import { SectionCard, Toggle } from "@cove-extensions/ui-shared";

import { DestinationField } from "./DestinationField";
import { kindSettings, nextKinds } from "./entityKindsLogic";
import {
  KIND_LABELS,
  NO_DESTINATION,
  RENAMABLE_KINDS,
  type Destination,
  type LibraryPathsState,
  type RenamableKind,
  type RenamerOptions,
} from "./options";

export interface EntityKindsSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
  library: LibraryPathsState;
}

export function EntityKindsSection({ options, set, library }: Readonly<EntityKindsSectionProps>) {
  const update = (kind: RenamableKind, enabled: boolean, destination: Destination | null) => {
    set("Kinds", nextKinds(options.Kinds, kind, enabled, destination));
  };

  return (
    <SectionCard title="Entity kinds">
      {RENAMABLE_KINDS.map((kind) => {
        const { Enabled: enabled, Destination: destination } = kindSettings(options.Kinds, kind);

        return (
          <div key={kind} className="space-y-2">
            <Toggle
              label={`Rename ${KIND_LABELS[kind].toLowerCase()}`}
              checked={enabled}
              onChange={(v) => {
                update(kind, v, destination);
              }}
            />
            {enabled && (
              <Toggle
                label="Send them to their own folder"
                checked={destination !== null}
                onChange={(v) => {
                  update(kind, enabled, v ? { ...NO_DESTINATION } : null);
                }}
                helper="Used when no tag, studio, source-path or unorganized rule matches the item. A matched rule still wins."
              />
            )}
            {enabled && destination !== null && (
              <DestinationField
                value={destination}
                onChange={(v) => {
                  update(kind, enabled, v);
                }}
                library={library}
                label={`${KIND_LABELS[kind]} folder template`}
              />
            )}
          </div>
        );
      })}
    </SectionCard>
  );
}
