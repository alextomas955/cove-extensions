/**
 * DestinationRoutingSection — the "Destination routing" card: default/unorganized destinations, the
 * per-studio and per-tag destination maps, advanced allowed-roots + source-path rules, the sidecar-
 * extension list, and the empty-folder cleanup toggle.
 *
 * Card ORDER is presentation only (default & unorganized first, then per-studio, per-tag, advanced,
 * then sidecar and empty-folder); it does NOT set the engine's rule-evaluation precedence, which is
 * decided server-side, so reordering these cards is safe. Presentational — every field flows up
 * through `set`.
 */
import { useState } from "react";
import { EntityReferenceValue } from "@cove/runtime/components";

import {
  toStringKeyed,
  fromStringKeyed,
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
  PathShapeHint,
  StatusText,
  extensionShapeAdvisory,
} from "@cove-extensions/ui-shared";
import { EntitySelectField } from "./EntitySelectField";
import { StudioDestinationsEditor } from "./StudioMap";

/** Strip one leading dot if present, then lowercase — the add-time transform for a sidecar extension. */
function normalizeSidecarExtension(raw: string): string {
  let v = raw.trim();
  if (v.startsWith(".")) v = v.slice(1);
  return v.toLowerCase();
}

export interface DestinationRoutingSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
}

export function DestinationRoutingSection({ options, set }: DestinationRoutingSectionProps) {
  // Live (not-yet-committed) AssociatedExtensions input, so the sidecar-extension advisory reflects
  // what the user is currently typing, before Enter commits it.
  const [sidecarLiveInput, setSidecarLiveInput] = useState("");

  return (
    <SectionCard
      title="Destination routing"
      description="Where renamed files land. Per-studio and per-tag rules override the default."
    >
      <GroupCard title="Default destination" description="The root most of your library lands in.">
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Field
            label="Default destination"
            helper="Where an item matching no rule goes. Blank = no default route. Honored only with the relocate gate below ON."
          >
            <TextInput
              value={options.DefaultDestination}
              onChange={(v) => {
                set("DefaultDestination", v);
              }}
              placeholder="Absolute root, or blank"
            />
            <PathShapeHint value={options.DefaultDestination} />
          </Field>
          <Field
            label="Unorganized destination"
            helper="Where un-curated items route instead of being skipped. Blank = no unorganized route."
          >
            <TextInput
              value={options.UnorganizedDestination}
              onChange={(v) => {
                set("UnorganizedDestination", v);
              }}
              placeholder="Absolute root, or blank"
            />
            <PathShapeHint value={options.UnorganizedDestination} />
          </Field>
        </div>
        <Toggle
          label="Relocate unmatched items to the default destination"
          checked={options.EnableDefaultRelocate}
          onChange={(v) => {
            set("EnableDefaultRelocate", v);
          }}
          helper="Moves every item matching no rule to the default destination — whole-library reach, which is too large to record an undo. Dry-run first. Off by default."
        />
      </GroupCard>

      <ToggleHeaderCard
        title="Per-studio destinations"
        description="Pick a studio, then the absolute root its items route to."
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
        />
      </ToggleHeaderCard>

      <ToggleHeaderCard
        title="Per-tag destinations"
        description="Pick a tag, then the absolute root its items route to."
        enabled={options.EnableTagDestinations}
        onToggle={(v) => {
          set("EnableTagDestinations", v);
        }}
      >
        {/* The tag id must stay a NUMBER end to end to keep the persisted map value-equal with the
            backend field and normalizeOptions, so it crosses the string-keyed KeyValueMapEditor
            boundary through the same explicit coercion the studio map uses. The host resolves a
            committed row's opaque id to the tag's name: one cached lookup per configured rule,
            bounded by the rules the user authored rather than by the library. */}
        <KeyValueMapEditor
          map={toStringKeyed(options.TagDestinations)}
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
            <>
              <TextInput value={value} onChange={setValue} placeholder="Destination root" />
              <PathShapeHint value={value} />
            </>
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
            A rename may only write inside these absolute directories; a target outside them is
            rejected. Empty = files stay within their own source folder.
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
            Match an item&apos;s source path to a destination root, top rule first. An exact match
            or a regex.
          </p>
          <ObjectArrayEditor<PathDestinationRule>
            rows={options.PathDestinations}
            onChange={(rows) => {
              set("PathDestinations", rows);
            }}
            makeRow={() => ({ Pattern: "", Dest: "", IsRegex: false })}
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
                <Field label="Destination root">
                  <TextInput
                    value={row.Dest}
                    onChange={(v) => {
                      update({ Dest: v });
                    }}
                    placeholder="Destination root"
                  />
                  <PathShapeHint value={row.Dest} />
                </Field>
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
