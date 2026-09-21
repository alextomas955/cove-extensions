// Seeds one scene into a running Whisparr's own catalogue, on either generation.
//
// The datastore rather than the add route, for the reason the shared entity seeder gives: an add
// resolves its identifier against the vendor's metadata service, which is a third party no sealed
// run controls. The two generations keep different catalogues - a movie row on v3, a series and an
// episode under it on v2 - so each has its own seeder here.
//
// A committed Python file is copied in and run, never a script assembled in the shell: a heredoc
// carries CRLF into every path it handles, and the failure then blames the path.
import { join } from "node:path";

/** Which generations a seeder here is written for; each seeder knows its own database path. */
const SEEDABLE_GENERATIONS = ["v3", "v2"];

const DATER_SOURCE = join(import.meta.dirname, "date-seeded-scene.py");
const DATER_TARGET = "/opt/harness/date-seeded-scene.py";
const V2_SEEDER_SOURCE = join(import.meta.dirname, "seed-v2-scene.py");
const V2_SEEDER_TARGET = "/opt/harness/seed-v2-scene.py";

/** The app's own user, which a copied file has to be handed to before the app can run it. */
const APP_USER = "1000:1000";

/**
 * The date this suite's scenes are dated, and the site they belong to.
 *
 * One pair, because the indexer stub echoes whatever date the query carries and the instance matches
 * a release to a scene on the two together. Two specs disagreeing about either would each search for
 * something the other's stub does not answer.
 */
export const SCENE_RELEASE_DATE = "2025-01-01";
export const SCENE_SITE = "Tushy Raw";

/**
 * Gives a seeded scene the site and date its instance searches by.
 *
 * The shared entity seeder writes only the columns the schema declares NOT NULL, which leaves a scene
 * with neither. This generation builds its search query from those two and nothing else, so without
 * them the interactive search answers an empty list having asked no indexer anything.
 */
export async function dateSeededScene(container, generation, foreignId) {
  if (!SEEDABLE_GENERATIONS.includes(generation)) {
    throw new Error(`dateSeededScene: no database is declared for generation "${generation}".`);
  }

  await container.copyFilesToContainer([{ source: DATER_SOURCE, target: DATER_TARGET }]);
  await container.exec(["chown", APP_USER, DATER_TARGET], { user: "root" });

  // As the app's own user, so the write-ahead and shared-memory siblings it touches keep belonging to
  // the process that goes on using them.
  const written = await container.exec(
    [
      "python3",
      DATER_TARGET,
      "--generation",
      generation,
      "--foreign-id",
      foreignId,
      "--release-date",
      SCENE_RELEASE_DATE,
      "--studio-title",
      SCENE_SITE,
    ],
    { user: APP_USER },
  );
  if (written.exitCode !== 0) {
    throw new Error(`dateSeededScene: ${written.output.trim()}`);
  }

  // The columns it set and the columns the build declares. A caller prints this when an import went
  // unannounced: a name from the other lineage is skipped rather than written, and a skipped column
  // looks exactly like one that was written.
  return written.output.trim();
}

/**
 * Writes one v2 scene, which is a site and an episode under it, and answers their instance-side ids.
 *
 * Its own seeder because a scene is two rows on this generation and one on the other, and the shared
 * entity seeder is wired for the other. The site's identifier is the caller's so a spec can keep two
 * runs apart.
 *
 * `monitored` is the site's starting flag. A caller driving both monitoring directions seeds it off,
 * so the first gesture is the one that turns it on.
 */
export async function seedV2Scene(
  container,
  whisparrApi,
  { siteId, siteTitle, rootFolderPath, sceneExternalId, sceneTitle, monitored = true },
) {
  const profiles = await whisparrApi.get("/api/v3/qualityprofile");
  const profileId = (profiles.json ?? [])[0]?.id;
  if (profileId === undefined) {
    throw new Error("seedV2Scene: the instance offers no quality profile to seed against.");
  }

  await container.copyFilesToContainer([{ source: V2_SEEDER_SOURCE, target: V2_SEEDER_TARGET }]);
  await container.exec(["chown", APP_USER, V2_SEEDER_TARGET], { user: "root" });

  const written = await container.exec(
    [
      "python3",
      V2_SEEDER_TARGET,
      "--site-id",
      String(siteId),
      "--site-title",
      siteTitle,
      "--root-folder-path",
      rootFolderPath,
      "--quality-profile-id",
      String(profileId),
      "--scene-external-id",
      sceneExternalId,
      "--scene-title",
      sceneTitle,
      "--air-date",
      SCENE_RELEASE_DATE,
      "--monitored",
      monitored ? "true" : "false",
    ],
    { user: APP_USER },
  );
  if (written.exitCode !== 0) {
    throw new Error(`seedV2Scene: ${written.output.trim()}`);
  }
  return JSON.parse(written.output.trim());
}
