// Every capability each generation declares, in the spelling the wire carries.
//
// Transcribed by hand from the product's own table, and deliberately not derived from it: a list
// read off the product would assert that a list equals itself. Asserted whole rather than per entry,
// so a capability gained or lost is reported by the test that reads it rather than by a control that
// quietly stops appearing.
//
// The two differ in both directions. Six entries are held by both, six by v3 alone, and three by v2
// alone. v3 needs neither the site registration nor the site-row read, because there a studio
// arrives as a side effect of adding a scene; it has no implementation for the held-site read
// either, because it answers presence per scene rather than through a list of sites.

export const V2_CAPABILITIES = [
  "outOfBandCallbackSecret",
  "monitorStudio",
  "reflectOwnedFiles",
  "searchMonitored",
  "monitorScene",
  "registerOwnedSites",
  "readSiteSceneRows",
  "readHeldSites",
  "readInstanceFilesystem",
];

export const V3_CAPABILITIES = [
  "outOfBandCallbackSecret",
  "monitorStudio",
  "monitorPerformer",
  "registerMissingScenes",
  "reflectOwnedFiles",
  "searchMonitored",
  "readSceneStatus",
  "readSceneExclusions",
  "searchScene",
  "monitorScene",
  "excludeScene",
  "readInstanceFilesystem",
];
