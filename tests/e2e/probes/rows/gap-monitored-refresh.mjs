// Whether a MONITORED catalogue refresh populates everything the entity offers, or stops short.
//
// The unmonitored half of this is already settled: it populates nothing. The monitored half is what
// a later phase's count acceptance rests on, and it is the phase description's own named example of
// a limitation that has to be recorded rather than worked around.
//
// This row asks the vendor's metadata service to enumerate an entity, so it stays off the default
// path behind `requires.live`. Library-sized input is the standing assumption in this repo, so what
// is recorded is counts and one listing LENGTH, never a listing — and the subject is bounded by a
// ceiling checked while the refresh runs, since a ceiling applied to a finished result would only
// say what had already been pulled.
//
// The subject is chosen by trying candidates rather than by reading a size off the lookup: the
// lookup's own count fields describe the local library and report nothing about a candidate that has
// not been added, so they cannot tell a small entity from a large one or from an empty one. A
// candidate that populates nothing is not evidence of truncation, so the row moves on rather than
// recording it as the answer.
import { attemptUntil } from "../../lib/poll.mjs";

const COMMAND_PATH = "/api/v3/command";
const STUDIO_PATH = "/api/v3/studio";
const QUALITY_PROFILE_PATH = "/api/v3/qualityprofile";
const LOOKUP_PATH = "/api/v3/lookup/studio";

// Narrow on purpose: the vendor answers a broad term with its largest catalogues, and this row wants
// a subject it can afford to enumerate.
const LOOKUP_TERM = "nubiles";

// The refresh is abandoned once the entity's own reported total passes this. A cap on what gets
// RECORDED would be worse than useless: it turns a subject this row should not have touched into a
// plausible-looking answer.
const SUBJECT_CEILING = 200;

// How many candidates the row will add before giving up on finding one that populates anything. Each
// costs the vendor an enumeration, so the search is bounded rather than run to exhaustion.
const MAX_CANDIDATES_TRIED = 4;

const REFRESH_COMMAND = "RefreshStudios";
const TERMINAL_COMMAND_STATUSES = new Set(["completed", "failed", "aborted", "cancelled"]);

const REFRESH_TIMEOUT_MS = 300_000;
const REFRESH_INTERVAL_MS = 3_000;
const LOOKUP_TIMEOUT_MS = 90_000;
const LOOKUP_INTERVAL_MS = 5_000;
const PROFILE_TIMEOUT_MS = 60_000;
const PROFILE_INTERVAL_MS = 2_000;

const VERDICTS = ["full", "truncated", "empty", "inconclusive"];

// The count fields the entity reports about itself. Both the populated and the total side come from
// the instance's own bookkeeping, which is what the verdict below is a comparison of.
const COUNT_FIELDS = ["movieCount", "sceneCount", "totalMovieCount", "totalSceneCount"];

const countsOf = (studio) =>
  Object.fromEntries(COUNT_FIELDS.map((field) => [field, studio?.[field] ?? null]));

const totalOf = (counts) => (counts.totalMovieCount ?? 0) + (counts.totalSceneCount ?? 0);

/**
 * A gate that keeps this row's outbound-causing calls under the rate the install is configured for.
 *
 * The rate belongs to the metadata servers the fixture was configured from, so the row asks the same
 * service no faster than the machine that supplied the credentials already asks it.
 */
function pacer(providers) {
  const rates = providers.servers
    .map((server) => server.maxRequestsPerMinute)
    .filter((rate) => Number.isFinite(rate) && rate > 0);
  const requestsPerMinute = rates.length > 0 ? Math.min(...rates) : null;
  const minIntervalMs = requestsPerMinute === null ? 1_000 : Math.ceil(60_000 / requestsPerMinute);
  let last = 0;
  return {
    describe: { source: providers.source, requestsPerMinute, minIntervalMs },
    async wait() {
      const due = last + minIntervalMs - Date.now();
      if (due > 0) await new Promise((resolve) => setTimeout(resolve, due));
      last = Date.now();
    },
  };
}

const commandRoster = async (api) =>
  ((await api.get(COMMAND_PATH)).json ?? []).map((command) => ({
    id: command.id,
    name: command.name,
    status: command.status,
    result: command.result,
  }));

const readStudio = async (api, studioId) => (await api.get(`${STUDIO_PATH}/${studioId}`)).json;

/**
 * A profile an add can name, polled rather than read once.
 *
 * A freshly started instance answers this route before it has finished seeding its own defaults, so
 * a single read races the seed and reports an instance that has none.
 */
async function firstQualityProfileId(api) {
  const { settled, value, note } = await attemptUntil(
    async (_signal, record) => {
      const profiles = await api.get(QUALITY_PROFILE_PATH);
      record(`${profiles.status} ${profiles.contentType} with ${profiles.json?.length ?? 0}`);
      const id = profiles.json?.[0]?.id;
      return typeof id === "number" ? { value: id } : null;
    },
    {
      timeoutMs: PROFILE_TIMEOUT_MS,
      intervalMs: PROFILE_INTERVAL_MS,
      label: "gap-monitored-refresh firstQualityProfileId",
    },
  );
  if (!settled) {
    throw new Error(
      `gap-monitored-refresh: GET ${QUALITY_PROFILE_PATH} last answered ${note}, so no add can name a profile.`,
    );
  }
  return value;
}

/**
 * The candidates a subject is chosen from.
 *
 * Nothing identifying a candidate is recorded: these are real businesses, and this row needs a
 * subject to refresh rather than a subject to publish.
 */
async function candidates(api, pace) {
  await pace.wait();
  const { settled, value, note } = await attemptUntil(
    async (_signal, record) => {
      const response = await api.get(`${LOOKUP_PATH}?term=${LOOKUP_TERM}`);
      const results = Array.isArray(response.json) ? response.json : [];
      record(`${response.status} ${response.contentType} with ${results.length} result(s)`);
      return results.length > 0 ? { value: results } : null;
    },
    {
      timeoutMs: LOOKUP_TIMEOUT_MS,
      intervalMs: LOOKUP_INTERVAL_MS,
      label: "gap-monitored-refresh candidates",
    },
  );
  if (!settled) {
    throw new Error(
      `gap-monitored-refresh: ${LOOKUP_PATH} for "${LOOKUP_TERM}" last answered ${note}. The lookup is proxied to the vendor's metadata service, so an empty answer here is an unreachable or refusing service rather than an absent capability.`,
    );
  }
  return value.map((wrapper) => wrapper.studio);
}

/**
 * Waits out everything a just-issued refresh started, abandoning a subject past the ceiling.
 *
 * At least one new command must be present before the wait can settle. A roster that has not yet
 * shown the refresh would otherwise satisfy "everything it started has finished" over an empty set,
 * and the counts read straight afterwards would belong to a pass that had not begun.
 */
async function settleRefresh(api, { studioId, foreignId, idsBefore, startedAt }) {
  let abandonedAt = null;
  const { settled, value } = await attemptUntil(
    async (_signal, note) => {
      const running = totalOf(countsOf(await readStudio(api, studioId)));
      if (running > SUBJECT_CEILING) {
        abandonedAt = running;
        return { value: { reason: "the subject passed the ceiling", fresh: [] } };
      }
      const fresh = (await commandRoster(api)).filter((command) => !idsBefore.has(command.id));
      note(`${running} reported, ${fresh.length} new command(s)`);
      const allTerminal = fresh.every((command) =>
        TERMINAL_COMMAND_STATUSES.has(String(command.status).toLowerCase()),
      );
      return fresh.length > 0 && allTerminal
        ? { value: { reason: "the refresh and everything it started have finished", fresh } }
        : null;
    },
    {
      timeoutMs: REFRESH_TIMEOUT_MS,
      intervalMs: REFRESH_INTERVAL_MS,
      label: "gap-monitored-refresh settleRefresh",
    },
  );

  const fresh = value?.fresh ?? [];
  const abandoned = abandonedAt !== null;
  const counts = countsOf(await readStudio(api, studioId));
  // The one listing this row reads, and only for its length. Skipped once the subject is abandoned,
  // since that is the case where fetching it would be the unbounded read.
  const works = abandoned ? null : await api.get(`${STUDIO_PATH}/${foreignId}/works`);
  return {
    abandoned,
    counts,
    worksStatus: works?.status ?? null,
    worksLength: Array.isArray(works?.json) ? works.json.length : null,
    refresh: {
      reason: settled ? value.reason : "the window elapsed before the refresh finished",
      abandonedAtReportedTotal: abandonedAt,
      elapsedMs: Date.now() - startedAt,
      cascade: {
        count: fresh.length,
        failed: fresh.filter((command) => String(command.status).toLowerCase() !== "completed")
          .length,
        names: [...new Set(fresh.map((command) => command.name))].sort().join(" "),
        refreshRan: fresh.some((command) => command.name === REFRESH_COMMAND),
      },
    },
  };
}

/**
 * Adds one candidate with monitoring already on, and waits out the refresh the add starts.
 *
 * Monitoring is set in the add rather than turned on afterwards. Turning it on and then asking for a
 * refresh leaves the entity reporting nothing: the request is accepted and no refresh command ever
 * reaches the roster, so that route cannot tell an entity with nothing in it from one this row has
 * simply failed to refresh.
 */
async function tryCandidate(api, pace, { candidate, qualityProfileId, rootFolderPath }) {
  const startedAt = Date.now();
  const idsBefore = new Set((await commandRoster(api)).map((command) => command.id));
  await pace.wait();
  const created = await api.post(STUDIO_PATH, {
    ...candidate,
    qualityProfileId,
    rootFolderPath,
    monitored: true,
    // The catalogue listing is the subject. Monitoring its works would ask the instance for a
    // second, larger body of work this row has no question about.
    moviesMonitored: false,
    searchOnAdd: false,
  });
  if (created.status !== 201) {
    throw new Error(
      `gap-monitored-refresh: POST ${STUDIO_PATH} answered ${created.status} ${created.contentType}, so there is no subject to refresh.`,
    );
  }
  const studioId = created.json.id;
  const pass = await settleRefresh(api, {
    studioId,
    foreignId: candidate.foreignId,
    idsBefore,
    startedAt,
  });
  return {
    studioId,
    // The entity's own counts at the moment it was created, before the refresh the add started had
    // populated anything.
    before: countsOf(created.json),
    monitoredAtAdd: created.json.monitored === true,
    pass,
  };
}

/**
 * A second pass over an already-refreshed subject, and whether it actually ran.
 *
 * A refresh request is accepted whether or not a command follows it, so the pass is judged by
 * whether one reached the roster. A repeat that did not run is recorded as such rather than counted
 * as a pass that added nothing.
 */
async function repeatRefresh(api, pace, { studioId, foreignId }) {
  const startedAt = Date.now();
  const idsBefore = new Set((await commandRoster(api)).map((command) => command.id));
  await pace.wait();
  const commanded = await api.post(COMMAND_PATH, { name: REFRESH_COMMAND, studioIds: [studioId] });
  const settled = await settleRefresh(api, { studioId, foreignId, idsBefore, startedAt });
  return {
    ...settled,
    commandStatus: commanded.status,
    ran: settled.refresh.cascade.refreshRan,
  };
}

export const row = {
  id: "gap-monitored-refresh",
  label: "Whether a monitored catalogue refresh populates in full or stops short",
  requires: {
    cove: true,
    whisparr: ["v3"],
    seedHistory: false,
    support: [],
    // An add is refused unless its destination is a registered library root.
    rootFolder: true,
    network: true,
    // This row asks a third party to enumerate an entity, so it never runs unasked.
    live: true,
  },
  async run(ctx) {
    const api = ctx.whisparr.apiFor("v3");
    const pace = pacer(ctx.providers);
    const qualityProfileId = await firstQualityProfileId(api);
    const rootFolderPath = ctx.whisparr.v3.rootFolder;
    const found = await candidates(api, pace);

    const attempts = [];
    let chosen = null;
    for (const candidate of found.slice(0, MAX_CANDIDATES_TRIED)) {
      const attempt = await tryCandidate(api, pace, {
        candidate,
        qualityProfileId,
        rootFolderPath,
      });
      attempts.push({
        reportedAtLookup: totalOf(countsOf(candidate)),
        reportedAfterRefresh: totalOf(attempt.pass.counts),
        worksLength: attempt.pass.worksLength,
        abandoned: attempt.pass.abandoned,
      });
      if (attempt.pass.abandoned || totalOf(attempt.pass.counts) > 0) {
        chosen = { candidate, ...attempt };
        break;
      }
    }

    // A second pass over the chosen subject. A count that rises on a repeat is a first pass that did
    // not deliver everything. It decides nothing when the repeat did not run, since a request that
    // was folded into the finished pass adds nothing for a reason that has no bearing on the answer.
    const repeat =
      chosen === null || chosen.pass.abandoned
        ? null
        : await repeatRefresh(api, pace, {
            studioId: chosen.studioId,
            foreignId: chosen.candidate.foreignId,
          });

    const first = chosen?.pass ?? null;
    const last = repeat?.ran ? repeat : first;
    const reportedTotal = last === null ? 0 : totalOf(last.counts);
    const worksLength = last?.worksLength ?? null;
    const countsAgree = worksLength === reportedTotal;
    const grewOnRepeat =
      repeat === null || !repeat.ran ? null : totalOf(repeat.counts) > totalOf(first.counts);
    const everyStartedCommandCompleted =
      (first?.refresh.cascade.failed ?? 0) === 0 && (repeat?.refresh.cascade.failed ?? 0) === 0;
    const abandoned = Boolean(first?.abandoned || repeat?.abandoned);
    const windowElapsed = first !== null && first.refresh.reason.startsWith("the window elapsed");

    const verdict =
      abandoned || windowElapsed
        ? "inconclusive"
        : chosen === null || (worksLength === 0 && reportedTotal === 0)
          ? "empty"
          : countsAgree && everyStartedCommandCompleted && grewOnRepeat !== true
            ? "full"
            : "truncated";

    const whatCouldNotBeObserved = abandoned
      ? `The entity's own reported total passed the ceiling of ${SUBJECT_CEILING} while a refresh was running, so the row abandoned it rather than enumerate a subject of that size.`
      : windowElapsed
        ? "A refresh did not reach a terminal status inside the window, so the counts read afterwards belong to a pass that had not finished."
        : chosen === null
          ? `Every candidate tried populated nothing, so this run says a monitored refresh delivered no rows for any of them and says nothing about what a refresh does to a subject that has some.`
          : null;

    return {
      method: {
        verb: "POST",
        path: `${STUDIO_PATH} with monitoring on, whose refresh is polled through ${COMMAND_PATH}, then a repeat asked for with POST ${COMMAND_PATH} {"name":"${REFRESH_COMMAND}"}`,
        inputs: { lookupTerm: LOOKUP_TERM, ceiling: SUBJECT_CEILING },
      },
      verdict,
      observed: {
        verdictVocabulary: VERDICTS.join(" | "),
        ...(whatCouldNotBeObserved === null ? {} : { whatCouldNotBeObserved }),
        rateHonoured: pace.describe,
        subjectSelection: {
          lookupTerm: LOOKUP_TERM,
          candidateCount: found.length,
          candidatesReportingANonZeroTotalAtLookup: found.filter(
            (candidate) => totalOf(countsOf(candidate)) > 0,
          ).length,
          sizeSignalAtLookup: `${LOOKUP_PATH} carries the same count fields the added entity does, and reports them as zero for a candidate that is not in the library, so it cannot rank candidates by size.`,
          chosenBy: `each candidate in turn was added, monitored and refreshed; the first that reported anything became the subject, and the ceiling of ${SUBJECT_CEILING} bounds how large that subject may turn out to be`,
          candidatesTried: attempts.length,
          maxCandidatesTried: MAX_CANDIDATES_TRIED,
          attempts,
          subjectFound: chosen !== null,
          identityRecorded: false,
        },
        counts: {
          before: chosen?.before ?? null,
          afterFirstRefresh: first?.counts ?? null,
          afterRepeatRefresh: repeat?.counts ?? null,
        },
        monitoredBy: {
          field: "monitored",
          setAtAdd: chosen?.monitoredAtAdd ?? null,
          note: "Set in the add. Turning it on afterwards and then asking for a refresh was observed to leave the entity reporting nothing, with the request accepted and no refresh command appearing on the roster, while the same subject added with monitoring already on populated.",
        },
        refresh: {
          first: first?.refresh ?? null,
          repeat: repeat?.refresh ?? null,
          repeatRan: repeat?.ran ?? null,
          repeatCommandStatus: repeat?.commandStatus ?? null,
        },
        worksListing: {
          path: `${STUDIO_PATH}/{studioForeignId}/works`,
          status: last?.worksStatus ?? null,
          lengthAfterFirstRefresh: first?.worksLength ?? null,
          lengthAfterRepeatRefresh: repeat?.worksLength ?? null,
          bodyRecorded: false,
          unpaged: true,
        },
        comparison: {
          populated: worksLength,
          reportedTotal,
          countsAgree,
          everyStartedCommandCompleted,
          grewOnRepeat,
          verdictRestsOn:
            "the two counts agreeing and every command the refresh started completing; a repeat pass raising the count overrides both, and a repeat that never ran decides nothing",
          note: "Both numbers are the instance's own bookkeeping, so an entry the vendor offers and a pass never fetched is absent from both. The cascade's failure count and the repeat pass are what make that case visible.",
        },
        ...(verdict === "full"
          ? {}
          : {
              gapCandidate: {
                id: "GAP-3",
                observed: {
                  date: new Date().toISOString().slice(0, 10),
                  builds: { v3: ctx.builds.whisparr.v3.version },
                },
                axis: "build",
                surface: `a monitored studio's catalogue refresh, ${REFRESH_COMMAND}, and the counts the entity reports afterwards`,
                expected:
                  "MON-6: a count taken from an entity after a refresh is the count of what that entity offers.",
                observedBehaviour: `The pass ended because ${first?.refresh.reason ?? "no subject was found"}; the entity then reported ${reportedTotal} and its works listing held ${worksLength}, with ${first?.refresh.cascade.failed ?? 0} of ${first?.refresh.cascade.count ?? 0} commands the refresh started ending in something other than completion, and a repeat pass ${grewOnRepeat === null ? "that did not run" : grewOnRepeat ? "raising the count" : "adding nothing"}.`,
                blastRadius:
                  "MON-6's count acceptance, and any later assertion that a monitored entity's reported count equals what the provider offers.",
                acceptanceAdjustment:
                  "Assert that the reported count is non-decreasing across refreshes and that it matches the entity's own works listing, rather than that it equals the provider's catalogue. The bound is the agreement between the two numbers the instance reports plus the refresh cascade reporting no failure, since neither count sees an entry the pass never fetched.",
                recheckTrigger:
                  "any bump of the v3 reference in lib/whisparr-images.mjs; the row is re-answered by re-running it with --live.",
                axisNote:
                  "build, not generation: a refresh that stops short is a property of this image and may not survive the next one, so it is recorded and re-checked rather than designed around.",
              },
            }),
      },
    };
  },
};
