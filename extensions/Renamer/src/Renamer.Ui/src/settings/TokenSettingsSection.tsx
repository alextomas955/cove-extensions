/**
 * TokenSettingsSection — the "Token settings" card: per-token formatting for $performers, $tags,
 * $date, and $duration. Each group only renders when the current templates actually use that token
 * (so an unused token never shows noise); when none is used, an empty-state offers one-click token
 * insertion. Presentational — edits flow up through set/setMulti/insertToken.
 */
import {
  type RenamerOptions,
  type MultiValueOptions,
  type OverflowPolicy,
  type SortOrder,
} from "./options";
import {
  Field,
  FieldGroup,
  NumberInput,
  Select,
  SectionCard,
  GroupCard,
  Badge,
  Chip,
  ExampleSelect,
  SeparatorChips,
  ChipMultiSelect,
  OrderedPickToAdd,
  type ExampleOption,
  type SeparatorOption,
  type ValueOption,
} from "@cove-extensions/ui-shared";
import { EntitySelectField } from "./EntitySelectField";
import { templateUsesToken } from "./templateValidation";
import { optionsFor } from "./selectOptions";

const OVERFLOW_OPTIONS = optionsFor<OverflowPolicy>({
  dropAll: "Drop all when over the max",
  keepFirst: "Keep the first N",
});
// Sort orders are not interchangeable between the two groups: the engine only honors id/favorite
// ordering for performers (tags fall back to name ordering), so a tag Sort that offered them would
// silently no-op. Hence two distinct lists rather than one shared constant.
const PERFORMER_SORT_OPTIONS = optionsFor<SortOrder>({
  nameAsc: "Name (A→Z)",
  none: "Keep original order",
  idAsc: "By internal id",
  favoriteFirst: "Favorites first, then name",
});
const TAG_SORT_OPTIONS: readonly { value: SortOrder; label: string }[] = [
  { value: "nameAsc", label: "Name (A→Z)" },
  { value: "none", label: "Keep original order" },
];

// The fixed performer-gender set. The value is the C# enum name the backend matches (case-insensitive);
// the label is the friendly spelling. Shared by the ignore-genders multiselect and the gender-order
// ranking, so both offer exactly the genders the engine understands rather than free text.
const GENDER_OPTIONS: readonly ValueOption[] = [
  { value: "Male", label: "Male" },
  { value: "Female", label: "Female" },
  { value: "TransgenderMale", label: "Transgender male" },
  { value: "TransgenderFemale", label: "Transgender female" },
  { value: "Intersex", label: "Intersex" },
  { value: "NonBinary", label: "Non-binary" },
];

// Common DateFormat options; the example column uses the reference date 2026-03-12.
export const DATE_FORMAT_OPTIONS: readonly ExampleOption[] = [
  { value: "yyyy-MM-dd", example: "2026-03-12" },
  { value: "yyyy", example: "2026" },
  { value: "MM-dd-yyyy", example: "03-12-2026" },
  { value: "dd.MM.yyyy", example: "12.03.2026" },
  { value: "yyyy.MM.dd", example: "2026.03.12" },
];

// Common DurationFormat options; the example column uses the reference duration 1h 23m 45s.
// Values carry the engine's literal backslash escapes exactly (TS "hh\\-mm\\-ss" = literal hh\-mm\-ss).
// The engine renders a duration through TimeSpan.ToString, where `mm` is the minutes component
// rather than the total minutes.
export const DURATION_FORMAT_OPTIONS: readonly ExampleOption[] = [
  { value: String.raw`hh\-mm\-ss`, example: "01-23-45" },
  { value: String.raw`hh\.mm\.ss`, example: "01.23.45" },
  { value: String.raw`mm\-ss`, example: "23-45" },
];

// Common separators; each label makes the literal whitespace visible.
const SEPARATOR_OPTIONS: readonly SeparatorOption[] = [
  { value: ", ", label: "Comma + space ( , )" },
  { value: " · ", label: "Middot ( · )" },
  { value: " ", label: "Space ( ␣ )" },
  { value: " - ", label: "Dash ( - )" },
];

export interface TokenSettingsSectionProps {
  options: RenamerOptions;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
  setMulti: (group: "performers" | "tags", patch: Partial<MultiValueOptions>) => void;
  insertToken: (token: string) => void;
}

export function TokenSettingsSection({
  options,
  set,
  setMulti,
  insertToken,
}: TokenSettingsSectionProps) {
  const mv = (group: "performers" | "tags") => options[group];

  const usesPerformers = templateUsesToken(
    "performers",
    options.filenameTemplate,
    options.folderTemplate,
  );
  const usesTags = templateUsesToken("tags", options.filenameTemplate, options.folderTemplate);
  const usesDate = templateUsesToken("date", options.filenameTemplate, options.folderTemplate);
  const usesDuration = templateUsesToken(
    "duration",
    options.filenameTemplate,
    options.folderTemplate,
  );

  return (
    <SectionCard title="Token settings" description="Formatting for individual tokens.">
      {usesPerformers ? (
        <GroupCard title="Performers" badge={<Badge mono>$performers</Badge>}>
          <FieldGroup label="Separator">
            <SeparatorChips
              value={mv("performers").separator}
              onChange={(v) => {
                setMulti("performers", { separator: v });
              }}
              options={SEPARATOR_OPTIONS}
              customPlaceholder="Custom separator"
              ariaLabel="Performer separator"
            />
          </FieldGroup>
          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Max count">
              <NumberInput
                value={mv("performers").maxCount}
                min={0}
                placeholder="No limit"
                blankWhenZero
                onChange={(v) => {
                  setMulti("performers", { maxCount: v });
                }}
              />
            </Field>
            <Field label="On overflow">
              <Select
                value={mv("performers").onOverflow}
                onChange={(v) => {
                  setMulti("performers", { onOverflow: v });
                }}
                options={OVERFLOW_OPTIONS}
              />
            </Field>
          </div>
          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Sort" helper="The id and favorite orders apply to performers only.">
              <Select
                value={mv("performers").sort}
                onChange={(v) => {
                  setMulti("performers", { sort: v });
                }}
                options={PERFORMER_SORT_OPTIONS}
              />
            </Field>
            <FieldGroup
              label="Ignore genders"
              helper="Removed before the max-count cap. Performers with no gender are always kept."
            >
              <ChipMultiSelect
                options={GENDER_OPTIONS}
                values={mv("performers").ignoreGenders}
                onChange={(v) => {
                  setMulti("performers", { ignoreGenders: v });
                }}
              />
            </FieldGroup>
          </div>
          <FieldGroup label="Gender order" helper="Most-preferred first. Anyone else sorts last.">
            <OrderedPickToAdd
              options={GENDER_OPTIONS}
              values={mv("performers").genderOrder}
              onChange={(v) => {
                setMulti("performers", { genderOrder: v });
              }}
              addPrompt="Add a gender…"
              ariaLabel="Gender order"
            />
          </FieldGroup>
          <EntitySelectField
            entityType="performer"
            label="Only include"
            values={mv("performers").whitelistIds}
            onChange={(v) => {
              setMulti("performers", { whitelistIds: v });
            }}
            placeholder="Search performers…"
          />
          <EntitySelectField
            entityType="performer"
            label="Never include"
            values={mv("performers").blacklistIds}
            onChange={(v) => {
              setMulti("performers", { blacklistIds: v });
            }}
            placeholder="Search performers…"
          />
        </GroupCard>
      ) : null}

      {usesTags ? (
        <GroupCard title="Tags" badge={<Badge mono>$tags</Badge>}>
          <FieldGroup label="Separator">
            <SeparatorChips
              value={mv("tags").separator}
              onChange={(v) => {
                setMulti("tags", { separator: v });
              }}
              options={SEPARATOR_OPTIONS}
              customPlaceholder="Custom separator"
              ariaLabel="Tag separator"
            />
          </FieldGroup>
          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Max count">
              <NumberInput
                value={mv("tags").maxCount}
                min={0}
                placeholder="No limit"
                blankWhenZero
                onChange={(v) => {
                  setMulti("tags", { maxCount: v });
                }}
              />
            </Field>
            <Field label="On overflow">
              <Select
                value={mv("tags").onOverflow}
                onChange={(v) => {
                  setMulti("tags", { onOverflow: v });
                }}
                options={OVERFLOW_OPTIONS}
              />
            </Field>
          </div>
          <Field label="Sort">
            <Select
              value={mv("tags").sort}
              onChange={(v) => {
                setMulti("tags", { sort: v });
              }}
              options={TAG_SORT_OPTIONS}
            />
          </Field>
          <EntitySelectField
            entityType="tag"
            label="Only include"
            values={mv("tags").whitelistIds}
            onChange={(v) => {
              setMulti("tags", { whitelistIds: v });
            }}
            placeholder="Search tags…"
          />
          <EntitySelectField
            entityType="tag"
            label="Never include"
            values={mv("tags").blacklistIds}
            onChange={(v) => {
              setMulti("tags", { blacklistIds: v });
            }}
            placeholder="Search tags…"
          />
        </GroupCard>
      ) : null}

      {usesDate || usesDuration ? (
        <GroupCard
          badge={
            <Badge mono>
              {usesDate && usesDuration ? "$date · $duration" : usesDate ? "$date" : "$duration"}
            </Badge>
          }
          title={
            usesDate && usesDuration
              ? "Date & duration format"
              : usesDate
                ? "Date format"
                : "Duration format"
          }
        >
          {usesDate ? (
            <FieldGroup label="Date format" helper="e.g. yyyy-MM-dd">
              <ExampleSelect
                value={options.dateFormat}
                onChange={(v) => {
                  set("dateFormat", v);
                }}
                options={DATE_FORMAT_OPTIONS}
                customPlaceholder="yyyy-MM-dd"
                ariaLabel="Date format"
              />
            </FieldGroup>
          ) : null}
          {usesDuration ? (
            <FieldGroup label="Duration format">
              <ExampleSelect
                value={options.durationFormat}
                onChange={(v) => {
                  set("durationFormat", v);
                }}
                options={DURATION_FORMAT_OPTIONS}
                customPlaceholder="hh\-mm\-ss"
                ariaLabel="Duration format"
              />
            </FieldGroup>
          ) : null}
        </GroupCard>
      ) : null}

      {!usesPerformers && !usesTags && !usesDate && !usesDuration ? (
        <div className="rounded-xl border border-border bg-card p-6 text-center">
          <h3 className="text-base font-semibold text-foreground">
            No token-specific settings needed
          </h3>
          <p className="mx-auto mb-4 mt-1 max-w-md text-sm text-secondary">
            Your templates don&apos;t use $performers, $tags, $date or $duration. Add one and its
            options show up here.
          </p>
          <div className="flex flex-wrap justify-center gap-1">
            <Chip
              selected={false}
              mono
              onClick={() => {
                insertToken("{ - $performers}");
              }}
            >
              $performers
            </Chip>
            <Chip
              selected={false}
              mono
              onClick={() => {
                insertToken("{ - $tags}");
              }}
            >
              $tags
            </Chip>
            <Chip
              selected={false}
              mono
              onClick={() => {
                insertToken("{ - $date}");
              }}
            >
              $date
            </Chip>
            <Chip
              selected={false}
              mono
              onClick={() => {
                insertToken("{ [$duration]}");
              }}
            >
              $duration
            </Chip>
          </div>
        </div>
      ) : null}
    </SectionCard>
  );
}
