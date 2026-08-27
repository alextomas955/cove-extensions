/**
 * AdvancedSection — the "Advanced" section's panels, all collapsed by default: name cleanup
 * (illegal/space handling, case, ASCII), length & collisions, cross-drive concurrency, the
 * pre-routing excludes, and field rewriting & name shaping. Sits directly under the section header
 * rather than inside a card of its own, so the header is what names the group. Presentational —
 * every field flows up through `set`.
 */
import {
  type RenamerOptions,
  type CaseTransform,
  type ExcludeRule,
  type FieldReplaceRule,
} from "./options";
import {
  Field,
  TextInput,
  NumberInput,
  Select,
  Toggle,
  TagListInput,
  CollapsibleSection,
  GroupCard,
  ObjectArrayEditor,
  RegexValidity,
  SegmentedReplace,
  TokenPicker,
  ExampleSelect,
  type ExampleOption,
} from "@cove-extensions/ui-shared";
import { EntitySelectField } from "./EntitySelectField";
import { BARE_TOKENS } from "./templateValidation";
import { TokenAdvisory } from "./templateAdvisories";

const CASE_OPTIONS: readonly { value: CaseTransform; label: string }[] = [
  { value: "None", label: "None" },
  { value: "Lower", label: "lower case" },
  { value: "Title", label: "Title Case" },
];

// The 18 canonical token names a FieldReplaceRule may target, mirroring Engine/TemplateEngine.cs
// `Tokens`. The value is the canonical spelling the backend matches (case-insensitive); offering the
// closed set keeps a rule from targeting a token the engine never resolves.
const TOKEN_OPTIONS: readonly { value: string; label: string }[] = [
  "title",
  "studio",
  "parentStudio",
  "studioCode",
  "director",
  "bitrate",
  "date",
  "year",
  "height",
  "width",
  "resolution",
  "videoCodec",
  "audioCodec",
  "frameRate",
  "duration",
  "performers",
  "tags",
  "ext",
].map((t) => ({ value: t, label: t }));

// Common duplicate-suffix patterns; {n} = collision counter, shown via example.
const SUFFIX_FORMAT_OPTIONS: readonly ExampleOption[] = [
  { value: " ({n})", example: "name (1).mp4" },
  { value: "_{n}", example: "name_1.mp4" },
  { value: " - {n}", example: "name - 1.mp4" },
];

export interface AdvancedSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
}

export function AdvancedSection({ options, set }: AdvancedSectionProps) {
  return (
    <div className="space-y-4">
      <CollapsibleSection
        title="Clean up the name"
        summary="Illegal-character and space handling, case, ASCII"
      >
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Field label="Illegal-char replacement">
            <SegmentedReplace
              value={options.IllegalReplacement}
              onChange={(v) => {
                set("IllegalReplacement", v);
              }}
              stripLabel="Strip"
              replaceLabel="Replace with"
              stripHelper="Illegal characters are removed."
              replaceHelper="Each illegal character becomes this."
              inputPlaceholder="e.g. _"
            />
          </Field>
          <Field label="Space replacement">
            <SegmentedReplace
              value={options.SpaceReplacement}
              onChange={(v) => {
                set("SpaceReplacement", v);
              }}
              stripLabel="Keep spaces"
              replaceLabel="Replace with"
              stripHelper="Spaces are left as-is."
              replaceHelper="Each space becomes this."
              inputPlaceholder="e.g. _ or ."
            />
          </Field>
          <Field
            label="Remove characters"
            helper="Deleted before illegal-character handling, e.g. ,#"
          >
            <TextInput
              value={options.RemoveCharacters}
              onChange={(v) => {
                set("RemoveCharacters", v);
              }}
              placeholder="e.g. ,#"
            />
          </Field>
          <Field label="Case">
            <Select
              value={options.Case}
              onChange={(v) => {
                set("Case", v);
              }}
              options={CASE_OPTIONS}
            />
          </Field>
        </div>
        <Toggle
          label="ASCII transliterate"
          checked={options.AsciiTransliterate}
          onChange={(v) => {
            set("AsciiTransliterate", v);
          }}
          helper="Convert accented characters to plain ASCII."
        />
        <Toggle
          label="Normalize punctuation to ASCII"
          checked={options.NormalizePunctuation}
          onChange={(v) => {
            set("NormalizePunctuation", v);
          }}
          helper="Fold curly quotes, en/em dashes, and ellipses to plain ASCII."
        />
      </CollapsibleSection>

      <CollapsibleSection
        title="Length & collisions"
        summary="Length caps, what to drop when too long, duplicate suffix"
      >
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Field label="Filename max length">
            <NumberInput
              value={options.FilenameMax}
              min={1}
              onChange={(v) => {
                set("FilenameMax", v);
              }}
            />
          </Field>
          <Field label="Full-path max length">
            <NumberInput
              value={options.FullPathMax}
              min={1}
              onChange={(v) => {
                set("FullPathMax", v);
              }}
            />
          </Field>
        </div>
        <Field label="Drop order" helper="Fields dropped (top first) when the name is too long.">
          <TagListInput
            values={options.DropOrder}
            onChange={(v) => {
              set("DropOrder", v);
            }}
            ordered
            placeholder="Add field, press Enter"
          />
          <TokenPicker
            tokens={BARE_TOKENS}
            values={options.DropOrder}
            onAdd={(name) => {
              set(
                "DropOrder",
                options.DropOrder.includes(name) ? options.DropOrder : [...options.DropOrder, name],
              );
            }}
          />
          <TokenAdvisory values={options.DropOrder} />
        </Field>
        <Field
          label="Duplicate suffix format"
          helper="{n} = a counter added only when a name already exists, e.g. name (1).mp4."
        >
          <ExampleSelect
            value={options.DuplicateSuffixFormat}
            onChange={(v) => {
              set("DuplicateSuffixFormat", v);
            }}
            options={SUFFIX_FORMAT_OPTIONS}
            customPlaceholder=" ({n})"
          />
        </Field>
      </CollapsibleSection>

      <CollapsibleSection
        title="Cross-drive concurrency"
        summary="How many transfers and renames run at once"
      >
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Field
            label="Cross-volume concurrency"
            helper="Files copied across drives at once. 2 for hard drives; 4–8 if both are SSDs. Higher isn't always faster."
          >
            <NumberInput
              value={options.CrossVolumeConcurrency}
              min={1}
              max={16}
              onChange={(v) => {
                set("CrossVolumeConcurrency", v);
              }}
            />
          </Field>
          <Field
            label="Same-volume concurrency"
            helper="Same-drive renames are instant; the default is fine."
          >
            <NumberInput
              value={options.SameVolumeConcurrency}
              min={1}
              max={16}
              onChange={(v) => {
                set("SameVolumeConcurrency", v);
              }}
            />
          </Field>
        </div>
      </CollapsibleSection>

      {/* Excludes — the pre-routing skip list, sibling to routing so the two stay parallel.
            These are evaluated before any routing rule; a matching item is dropped from the batch
            entirely (neither renamed nor moved), so they are the safest way to fence off items you
            never want this extension to touch. All three flow through set() like every other control. */}
      <CollapsibleSection
        title="Excludes"
        summary="Skip items by tag, studio, or source path — evaluated before any routing"
      >
        <GroupCard title="Exclude by tag">
          <EntitySelectField
            entityType="tag"
            label="Tags"
            values={options.ExcludeTagIds}
            onChange={(v) => {
              set("ExcludeTagIds", v);
            }}
            placeholder="Search tags…"
          />
        </GroupCard>

        <GroupCard title="Exclude by studio" description="A child studio counts too.">
          <EntitySelectField
            entityType="studio"
            label="Studios"
            values={options.ExcludeStudioIds}
            onChange={(v) => {
              set("ExcludeStudioIds", v);
            }}
            placeholder="Search studios…"
          />
        </GroupCard>

        <GroupCard title="Exclude by source path" description="An exact match or a regex.">
          <ObjectArrayEditor<ExcludeRule>
            rows={options.ExcludePaths}
            onChange={(rows) => {
              set("ExcludePaths", rows);
            }}
            makeRow={() => ({ Pattern: "", IsRegex: false })}
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
              </>
            )}
            addLabel="Add exclude rule"
          />
        </GroupCard>
      </CollapsibleSection>

      {/* Field rewriting — shapes a token's value BEFORE the template renders (mirroring the
            ordering note on "Destination routing"/"Excludes"): literal per-token replaces, leading-
            article stripping, the name-shaping toggles, and the per-token whitespace map. All flow
            through set() like every other control. */}
      <CollapsibleSection
        title="Field rewriting & name shaping"
        summary="Literal token replacements, article stripping, and name shaping"
      >
        <GroupCard
          title="Per-token replacements"
          description="A literal find/replace on one token's value, before the name is shaped."
        >
          <ObjectArrayEditor<FieldReplaceRule>
            rows={options.FieldReplacers}
            onChange={(rows) => {
              set("FieldReplacers", rows);
            }}
            makeRow={() => ({ TargetToken: TOKEN_OPTIONS[0].value, Find: "", Replace: "" })}
            renderRow={(row, _i, update) => {
              // A rule saved before this dropdown existed (or via a hand-edited blob) may hold a
              // token outside the 18 — surface it as an extra option so the Select shows the real
              // stored value instead of silently displaying the first option while state differs.
              const tokenOptions = TOKEN_OPTIONS.some((o) => o.value === row.TargetToken)
                ? TOKEN_OPTIONS
                : [
                    ...TOKEN_OPTIONS,
                    { value: row.TargetToken, label: `${row.TargetToken} (unknown)` },
                  ];
              return (
                <>
                  <Field label="Target token">
                    <Select
                      value={row.TargetToken}
                      onChange={(v) => {
                        update({ TargetToken: v });
                      }}
                      options={tokenOptions}
                    />
                  </Field>
                  <Field label="Find" helper="Literal text to match. Empty does nothing.">
                    <TextInput
                      value={row.Find}
                      onChange={(v) => {
                        update({ Find: v });
                      }}
                      placeholder="Text to find"
                    />
                  </Field>
                  <Field label="Replace with">
                    <TextInput
                      value={row.Replace}
                      onChange={(v) => {
                        update({ Replace: v });
                      }}
                      placeholder="Replacement (blank to remove)"
                    />
                  </Field>
                </>
              );
            }}
            addLabel="Add replacement"
          />
        </GroupCard>

        <GroupCard title="Strip leading article">
          <Toggle
            label="Strip a leading article from the title"
            checked={options.StripLeadingArticles}
            onChange={(v) => {
              set("StripLeadingArticles", v);
            }}
            helper="Case-insensitive, and only a whole word at the start."
          />
          <Field label="Articles">
            <TagListInput
              values={options.Articles}
              onChange={(v) => {
                set("Articles", v);
              }}
              placeholder="Add article, press Enter"
            />
          </Field>
        </GroupCard>

        <Toggle
          label="Squeeze studio names"
          checked={options.SqueezeStudioNames}
          onChange={(v) => {
            set("SqueezeStudioNames", v);
          }}
          helper="So one studio renders to one stable folder name."
        />
        <Toggle
          label="Drop a performer already in the title"
          checked={options.PreventTitlePerformer}
          onChange={(v) => {
            set("PreventTitlePerformer", v);
          }}
          helper="Only when the whole name appears in the title."
        />
        <Toggle
          label="Collapse repeated folder segments"
          checked={options.PreventConsecutiveSegments}
          onChange={(v) => {
            set("PreventConsecutiveSegments", v);
          }}
          helper="Affects the folder path, not the filename."
        />
      </CollapsibleSection>
    </div>
  );
}
