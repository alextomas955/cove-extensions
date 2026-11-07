import { describe, expect, it } from "vitest";

import { CAP_UNAVAILABLE_ON_THIS_GENERATION, NO_INSTANCE_CONNECTED } from "../common/ui/copy";
import type { MissingEntryOffer } from "../wire/api";
import { missingEntryChoice } from "./missingEntryLogic";

const CONNECTED: MissingEntryOffer = {
  modes: ["trackOnly", "monitorNewReleases", "monitorAllScenes"],
  scopeChangeIsRetroactive: false,
};

describe("the ways one entity can be added", () => {
  it("names the three modes in its own order, the cheapest first", () => {
    const choice = missingEntryChoice(CONNECTED);

    expect(choice.rows.map((row) => row.mode)).toEqual([
      "trackOnly",
      "monitorNewReleases",
      "monitorAllScenes",
    ]);
    expect(choice.rows.map((row) => row.label)).toEqual([
      "Track only",
      "New releases only",
      "All scenes",
    ]);
    expect(choice.reason).toBeNull();
  });

  // The order is this module's own, so the cheap option does not move under the cursor when the
  // server answers the same modes in another order.
  it("keeps that order whatever order the offer named the modes in", () => {
    const choice = missingEntryChoice({
      ...CONNECTED,
      modes: ["monitorAllScenes", "trackOnly", "monitorNewReleases"],
    });

    expect(choice.rows.map((row) => row.mode)).toEqual([
      "trackOnly",
      "monitorNewReleases",
      "monitorAllScenes",
    ]);
  });

  it("offers no scope choice where the offer named one monitor mode", () => {
    const choice = missingEntryChoice({ ...CONNECTED, modes: ["trackOnly", "monitorAllScenes"] });

    expect(choice.rows.map((row) => row.mode)).toEqual(["trackOnly", "monitorAllScenes"]);
    expect(choice.rows.map((row) => row.label)).toEqual(["Track only", "All scenes"]);
  });

  it("marks the back catalogue on one row and on no other", () => {
    const marking = missingEntryChoice(CONNECTED)
      .rows.filter((row) => row.marksTheBackCatalogue)
      .map((row) => row.mode);

    expect(marking).toEqual(["monitorAllScenes"]);
  });

  it("says no instance answered where the offer settled the scope question neither way", () => {
    const choice = missingEntryChoice({ modes: [], scopeChangeIsRetroactive: null });

    expect(choice.rows).toEqual([]);
    expect(choice.reason).toBe(NO_INSTANCE_CONNECTED);
  });

  it("says the connected instance reaches this entity through nothing", () => {
    const choice = missingEntryChoice({ modes: [], scopeChangeIsRetroactive: true });

    expect(choice.rows).toEqual([]);
    expect(choice.reason).toBe(CAP_UNAVAILABLE_ON_THIS_GENERATION);
  });

  it("carries the one-way door from the connection the offer was read over", () => {
    expect(missingEntryChoice(CONNECTED).oneWayDoor).toBe(true);
    expect(missingEntryChoice({ ...CONNECTED, scopeChangeIsRetroactive: true }).oneWayDoor).toBe(
      false,
    );
    expect(missingEntryChoice({ ...CONNECTED, scopeChangeIsRetroactive: null }).oneWayDoor).toBe(
      false,
    );
  });
});
