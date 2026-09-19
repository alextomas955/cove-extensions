/**
 * A complete options object, for a test that has to pass one and does not care what it holds.
 *
 * The values are test input, not the product's defaults. The defaults live in the C# record and reach
 * the panel from the options endpoint, so a panel-side copy of them would be a second declaration free
 * to drift. A test that depends on a particular value sets that one and leaves the rest.
 */
import type { RenamerOptions } from "./options";

export function someOptions(): RenamerOptions {
  return {
    filenameTemplate: "{$date - }$title",
    folderTemplate: "",
    folderRoot: "",
    dateFormat: "yyyy-MM-dd",
    durationFormat: String.raw`hh\-mm\-ss`,
    performers: {
      separator: " ",
      maxCount: 0,
      onOverflow: "dropAll",
      sort: "nameAsc",
      whitelistIds: [],
      blacklistIds: [],
      ignoreGenders: [],
      genderOrder: [],
    },
    tags: {
      separator: " ",
      maxCount: 0,
      onOverflow: "dropAll",
      sort: "nameAsc",
      whitelistIds: [],
      blacklistIds: [],
      ignoreGenders: [],
      genderOrder: [],
    },
    illegalReplacement: "",
    spaceReplacement: "",
    removeCharacters: "",
    case: "none",
    asciiTransliterate: false,
    normalizePunctuation: true,
    filenameMax: 255,
    fullPathMax: 259,
    crossVolumeConcurrency: 2,
    sameVolumeConcurrency: 8,
    freeSpaceHeadroomBytes: 1073741824,
    dropOrder: [],
    onlyOrganized: false,
    filenameAsTitle: true,
    requiredFields: [],
    duplicateSuffixFormat: " ({n})",
    autoRenamerOnUpdate: false,
    studioDestinations: {},
    tagDestinations: {},
    pathDestinations: [],
    excludeTagIds: [],
    excludeStudioIds: [],
    excludePaths: [],
    associatedExtensions: [],
    unorganizedDestination: null,
    removeEmptyFolder: false,
    squeezeStudioNames: false,
    fieldReplacers: [],
    stripLeadingArticles: false,
    articles: [],
    preventTitlePerformer: false,
    preventConsecutiveSegments: true,
    kinds: {},
  };
}
