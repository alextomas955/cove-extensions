/**
 * DestinationRoutingSection — the "Destination routing" card: the unorganized destination, the
 * per-studio and per-tag destination maps, advanced allowed-roots + source-path rules, the sidecar-
 * extension list, and the empty-folder cleanup toggle.
 *
 * Card ORDER is presentation only (unorganized first, then per-studio, per-tag, advanced,
 * then sidecar and empty-folder); it does NOT set the engine's rule-evaluation precedence, which is
 * decided server-side, so reordering these cards is safe. Presentational — every field flows up
 * through `set`.
 */
import { useState } from "react";
import { EntityReferenceValue } from "@cove/runtime/components";

import {
  toStringKeyed,
  fromStringKeyed,
  newDestination,
  type LibraryPathsState,
  type RenamerOptions,
  type PathDestinationRule,
} from "./options";
import {
  Field,
  TextInput,
  Toggle,
  TagListInput,
  GroupCard,
  SectionCard,
  ToggleHeaderCard,
  KeyValueMapEditor,
  ObjectArrayEditor,
  RegexValidity,
  StatusText,
  extensionShapeAdvisory,
} from "@cove-extensions/ui-shared";
import { EntitySelectField } from "./EntitySelectField";
import { StudioDestinationsEditor } from "./StudioMap";
import { DestinationField } from "./DestinationField";

/** Strip one leading dot if present, then lowercase — the add-time transform for a sidecar extension. */
function normalizeSidecarExtension(raw: string): string {
  let v = raw.trim();
  if (v.startsWith(".")) v = v.slice(1);
  return v.toLowerCase();
}

export interface DestinationRoutingSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
  /** Cove's configured library paths, fetched once at the panel root. */
  library: LibraryPathsState;
}

export function DestinationRoutingSection({
  options,
  set,
  library,
}: DestinationRoutingSectionProps) {
  // Live (not-yet-committed) AssociatedExtensions input, so the sidecar-extension advisory reflects
  // what the user is currently typing, before Enter commits it.
  const [sidecarLiveInput, setSidecarLiveInput] = useState("");

  return (
    <SectionCard
      title="Destination routing"
      description="Where renamed files land. An item matching no rule follows Where files go."
    >
      <GroupCard
        title="Unorganized destination"
        description="Where items you have not curated yet land."
      >
        <Toggle
          label="Route un-curated items"
          checked={options.UnorganizedDestination !== null}
          onChange={(on) => {
            set("UnorganizedDestination", on ? newDestination(library.paths) : null);
          }}
          helper="Off = no unorganized route, and an un-curated item is skipped by Only organized."
        />
        {options.UnorganizedDestination !== null ? (
          <DestinationField
            value={options.UnorganizedDestination}
            onChange={(v) => {
              set("UnorganizedDestination", v);
            }}
            library={library}
            label="Folder"
            helper="Rendered under the root above. Blank = the root itself."
          />
        ) : null}
      </GroupCard>

      <ToggleHeaderCard
        title="Per-studio destinations"
        description="Pick a studio, then where its items go."
        enabled={options.EnableStudioDestinations}
        onToggle={(v) => {
          set("EnableStudioDestinations", v);
        }}
      >
        <StudioDestinationsEditor
          map={options.StudioDestinations}
          onChange={(m) => {
            set("StudioDestinations", m);
          }}
          library={library}
        />
      </ToggleHeaderCard>

      <ToggleHeaderCard
        title="Per-tag destinations"
        description="Pick a tag, then where its items go."
        enabled={options.EnableTagDestinations}
        onToggle={(v) => {
          set("EnableTagDestinations", v);
        }}
      >
        {/* The map keys on the stable tag id, so it crosses the string-keyed KeyValueMapEditor
            boundary through the same explicit coercion the studio map uses — the id must stay a
            NUMBER end to end to stay value-equal with the backend field and normalizeOptions.
            A committed row therefore shows an opaque id, which the host resolves to the tag's name:
            one cached lookup per configured rule, bounded by the rules the user authored rather than
            by the library. */}
        <KeyValueMapEditor
          map={toStringKeyed(options.TagDestinations)}
          emptyValue={newDestination(library.paths)}
          onChange={(m) => {
            set("TagDestinations", fromStringKeyed(m));
          }}
          renderKey={(draftKey, setDraftKey, existingKeys) => (
            <EntitySelectField
              entityType="tag"
              label=""
              values={draftKey === "" ? [] : [Number(draftKey)]}
              onChange={(values) => {
                // Last-id-wins: the selector is multi-value but a map key holds exactly one tag.
                const latest = values.at(-1);
                setDraftKey(latest === undefined ? "" : String(latest));
              }}
              placeholder="Search tags…"
              excludeIds={existingKeys.map(Number)}
            />
          )}
          renderValue={(value, setValue) => (
            <DestinationField value={value} onChange={setValue} library={library} label="Folder" />
          )}
          renderKeyLabel={(key) => <EntityReferenceValue entityType="tag" value={Number(key)} />}
          addLabel="Add tag rule"
        />
      </ToggleHeaderCard>

      <ToggleHeaderCard
        title="Advanced routing & safety"
        description="Allowed roots and source-path rules."
        enabled={options.EnableAdvancedRouting}
        onToggle={(v) => {
          set("EnableAdvancedRouting", v);
        }}
      >
        <div>
          <h4 className="text-sm font-semibold text-foreground">Allowed roots</h4>
          <p className="mb-4 mt-1 text-sm text-secondary">
            Narrows where a rename may write to these absolute directories. Every destination is
            already inside a Cove library path, so this can only make that area smaller — never
            larger. Empty = no narrowing.
          </p>
          <TagListInput
            values={options.AllowedRoots}
            onChange={(v) => {
              set("AllowedRoots", v);
            }}
            placeholder="Add an absolute directory, press Enter"
          />
        </div>

        <div>
          <h4 className="text-sm font-semibold text-foreground">Source-path destinations</h4>
          <p className="mb-4 mt-1 text-sm text-secondary">
            Match an item&apos;s source path to a destination, top rule first. An exact match or a
            regex.
          </p>
          <ObjectArrayEditor<PathDestinationRule>
            rows={options.PathDestinations}
            onChange={(rows) => {
              set("PathDestinations", rows);
            }}
            makeRow={() => ({ Pattern: "", Dest: newDestination(library.paths), IsRegex: false })}
            renderRow={(row, _i, update) => (
              <>
                <Field label="Source path">
                  <TextInput
                    value={row.Pattern}
                    onChange={(v) => {
                      update({ Pattern: v });
                    }}
                    mono
                    placeholder="Exact path or regex"
                  />
                </Field>
                <Toggle
                  label="Match as a regex"
                  checked={row.IsRegex}
                  onChange={(v) => {
                    update({ IsRegex: v });
                  }}
                />
                <RegexValidity pattern={row.Pattern} isRegex={row.IsRegex} />
                <DestinationField
                  value={row.Dest}
                  onChange={(v) => {
                    update({ Dest: v });
                  }}
                  library={library}
                  label="Folder"
                />
              </>
            )}
            addLabel="Add path rule"
            ordered
          />
        </div>
      </ToggleHeaderCard>

      <GroupCard
        title="Sidecar files"
        description="Files sharing the primary's basename with these extensions move and rename with it — an existing target is never overwritten. Cove-tracked captions always move."
      >
        <Field label="Also move sidecar files with these extensions">
          <TagListInput
            values={options.AssociatedExtensions}
            onChange={(v) => {
              set("AssociatedExtensions", v);
            }}
            placeholder="Add an extension, press Enter"
            normalize={normalizeSidecarExtension}
            onReject={(candidate) => !/^[a-z0-9]+$/.test(candidate)}
            onLiveChange={(raw) => {
              setSidecarLiveInput(raw);
            }}
          />
          {(() => {
            const advisory = extensionShapeAdvisory(normalizeSidecarExtension(sidecarLiveInput));
            return advisory ? <StatusText kind="warning">{advisory}</StatusText> : null;
          })()}
        </Field>
      </GroupCard>

      <div className="rounded-xl border border-border bg-card p-4">
        <Toggle
          label="Delete the source folder when a move leaves it empty"
          checked={options.RemoveEmptyFolder}
          onChange={(v) => {
            set("RemoveEmptyFolder", v);
          }}
          helper="Deletes a source folder only when a move leaves it empty — never a non-empty folder or a root. Undo won't recreate a deleted folder. Off by default."
        />
      </div>
    </SectionCard>
  );
}
