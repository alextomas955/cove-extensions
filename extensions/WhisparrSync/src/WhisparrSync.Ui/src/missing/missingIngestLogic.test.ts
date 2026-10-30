import { describe, expect, it } from "vitest";

import { scenesStillArriving } from "./missingIngestLogic";

describe("whether Whisparr is still building the entity's catalogue", () => {
  it("names both figures while it lists fewer than the source counts", () => {
    expect(
      scenesStillArriving({ instanceCatalogueCount: 340, providerCatalogueCount: 1205 }),
    ).toEqual({ listed: 340, counted: 1205 });
  });

  it("says nothing where the source answered no figure", () => {
    expect(
      scenesStillArriving({ instanceCatalogueCount: 340, providerCatalogueCount: null }),
    ).toBeNull();
  });

  it("says nothing where the source counts nothing", () => {
    expect(
      scenesStillArriving({ instanceCatalogueCount: 0, providerCatalogueCount: 0 }),
    ).toBeNull();
  });

  it("says nothing once Whisparr lists at least what the source counts", () => {
    expect(
      scenesStillArriving({ instanceCatalogueCount: 1205, providerCatalogueCount: 1205 }),
    ).toBeNull();
    expect(
      scenesStillArriving({ instanceCatalogueCount: 1210, providerCatalogueCount: 1205 }),
    ).toBeNull();
  });
});
