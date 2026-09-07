// Seeds a Whisparr instance with a known, deliberately-shaped movie set from the synthetic corpus in
// fixtures/wire-seed/, and hands back named descriptors so a spec asserts on identity rather than on a
// row's position in a response.
//
// The shapes exist to make specific questions answerable that an empty or uniform instance cannot answer:
// a studio and a performer that are KNOWN to Whisparr yet hold zero movies, a movie whose StashDB id
// differs from its foreign id, two rows carrying the same StashDB id, and a performer credited twice on
// one row.

/** A studio and a performer are made known-but-empty by adding a movie under them and deleting it again. */
const ROOT_FOLDER_FALLBACK = '/data/media';

export const SEED_IDS = {
  studioWithMovies: 'a0000000-0000-4000-8000-000000000001',
  studioWithNoMovies: 'a0000000-0000-4000-8000-000000000002',
  studioUnknown: 'a0000000-0000-4000-8000-0000000000ff',
  performerWithMovies: 'c0000000-0000-4000-8000-000000000001',
  performerWithNoMovies: 'c0000000-0000-4000-8000-000000000002',
  performerCreditedTwice: 'c0000000-0000-4000-8000-000000000003',
  performerUnknown: 'c0000000-0000-4000-8000-0000000000ff',
  sceneOne: '10000000-0000-4000-8000-000000000001',
  sceneTwo: '10000000-0000-4000-8000-000000000002',
  sceneThree: '10000000-0000-4000-8000-000000000003',
  sceneBorealis: '10000000-0000-4000-8000-0000000000b1',
  sceneAbsent: '10000000-0000-4000-8000-0000000000ff',
  movieDifferingForeignId: '999001',
  movieDifferingStashId: '20000000-0000-4000-8000-000000000001',
  movieDuplicateForeignId: '999002',
  // The three studio-attribution delta classes. Each is one row that makes one specific disagreement between
  // the title predicate and the identity predicate observable; without them a corpus can only show agreement.
  studioDivergentTitle: 'a0000000-0000-4000-8000-000000000003',
  studioNoTitle: 'a0000000-0000-4000-8000-000000000004',
  sceneAuroraNoStudioIdentity: '10000000-0000-4000-8000-000000000011',
  sceneDivergentStudioTitle: '10000000-0000-4000-8000-000000000021',
  sceneNoStudioTitle: '10000000-0000-4000-8000-000000000031',
  // The one row served with a cover image, under a studio nothing else references and with no credits, so
  // a cover-narrowing parameter becomes decidable without moving any other measurement's counts.
  sceneWithCover: '10000000-0000-4000-8000-0000000000c1',
  studioWithCoverScene: 'a0000000-0000-4000-8000-000000000005',
};

/**
 * Adds the corpus to {@link instance} and returns descriptors plus the resulting set size, which is read
 * back from the instance rather than counted from what was sent.
 *
 * @returns {Promise<{ setSize: number, rows: object[], added: object[], deleted: object[] }>}
 */
export async function seedWireCorpus(instance) {
  const roots = await instance.api('GET', '/api/v3/rootfolder');
  const rootFolderPath = roots.json?.[0]?.path ?? ROOT_FOLDER_FALLBACK;
  const profiles = await instance.api('GET', '/api/v3/qualityprofile');
  const qualityProfileId = profiles.json?.[0]?.id ?? 1;

  async function add({ foreignId, stashId, title }) {
    const res = await instance.api('POST', '/api/v3/movie', {
      foreignId,
      ...(stashId === undefined ? {} : { stashId }),
      title,
      monitored: false,
      qualityProfileId,
      rootFolderPath,
      // Loop-safety: nothing this fixture adds may provoke a grab, and the instance carries no indexer
      // to grab from either.
      addOptions: { searchForMovie: false },
    });
    if (res.status !== 201) {
      throw new Error(
        `seedWireCorpus: adding ${title} (${foreignId}) answered ${res.status}: ` +
          String(res.json?.message ?? res.text).split('\n')[0],
      );
    }
    return res.json;
  }

  const added = [];
  added.push(await add({ foreignId: SEED_IDS.sceneOne, stashId: SEED_IDS.sceneOne, title: 'Aurora Scene One' }));
  added.push(await add({ foreignId: SEED_IDS.sceneTwo, stashId: SEED_IDS.sceneTwo, title: 'Aurora Scene Two' }));
  added.push(await add({ foreignId: SEED_IDS.sceneThree, stashId: SEED_IDS.sceneThree, title: 'Aurora Scene Three' }));
  added.push(await add({ foreignId: SEED_IDS.movieDifferingForeignId, title: 'Aurora Movie' }));
  added.push(await add({ foreignId: SEED_IDS.movieDuplicateForeignId, title: 'Aurora Movie Duplicate' }));
  added.push(await add({
    foreignId: SEED_IDS.sceneAuroraNoStudioIdentity, stashId: SEED_IDS.sceneAuroraNoStudioIdentity,
    title: 'Aurora Scene Untied',
  }));
  added.push(await add({
    foreignId: SEED_IDS.sceneDivergentStudioTitle, stashId: SEED_IDS.sceneDivergentStudioTitle,
    title: 'Cirrus Scene One',
  }));
  added.push(await add({
    foreignId: SEED_IDS.sceneNoStudioTitle, stashId: SEED_IDS.sceneNoStudioTitle,
    title: 'Echo Scene One',
  }));
  added.push(await add({
    foreignId: SEED_IDS.sceneWithCover, stashId: SEED_IDS.sceneWithCover,
    title: 'Cascade Cover Scene',
  }));

  // The known-but-empty pair: adding this row is what registers Studio Borealis and Ivy Calloway as
  // entities Whisparr knows; deleting it again is what leaves them holding nothing. There is no way to
  // register an entity without a movie — a bare POST /studio resolves its metadata by foreign id and
  // creates the same row the add already created.
  const borealis = await add({ foreignId: SEED_IDS.sceneBorealis, stashId: SEED_IDS.sceneBorealis, title: 'Borealis Scene' });
  const removal = await instance.api(
    'DELETE',
    `/api/v3/movie/${borealis.id}?deleteFiles=false&addImportExclusion=false`,
  );
  if (removal.status !== 200) {
    throw new Error(`seedWireCorpus: removing the Borealis row answered ${removal.status}`);
  }

  const index = await instance.api('GET', '/api/v3/movie');
  const rows = index.json ?? [];
  return { setSize: rows.length, rows, added, deleted: [borealis] };
}

/** The Whisparr row id for a seeded foreign id, so a spec addresses a row by identity rather than position. */
export function rowByForeignId(rows, foreignId) {
  return rows.find((row) => row.foreignId === String(foreignId));
}

// ---- A corpus whose SIZE is the variable, for a per-row cost that has to be read at two sizes ----

/** The studio every generated scene is attributed to; it exists so a generated row is shaped like a real one. */
export const GENERATED_STUDIO_ID = 'a0000000-0000-4000-8000-0000000000aa';

/** The StashDB id of the nth generated scene, one-based. */
export function generatedSceneId(n) {
  return `20000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
}

const GENERATED_STUDIO = {
  Created: '2020-01-01T00:00:00Z',
  Updated: '2020-01-01T00:00:00Z',
  ExtractedAt: '2026-08-03T00:00:00Z',
  Overview: '',
  Title: 'Studio Meridian',
  Aliases: [],
  Slug: 'studio-meridian',
  Images: [],
  Year: 0,
  Network: null,
  NetworkForeignIds: null,
  Status: 0,
  OriginalLanguage: null,
  Homepage: null,
  SubStudios: [],
  ForeignIds: { TmdbId: 0, StashId: GENERATED_STUDIO_ID, TpdbId: null },
};

function generatedScene(n) {
  const stashId = generatedSceneId(n);
  return {
    Created: '2026-01-01T00:00:00Z',
    Updated: '2026-01-01T00:00:00Z',
    ExtractedAt: '2026-08-03T00:00:00Z',
    ItemType: 1,
    Year: 2026,
    AlternativeTitles: [],
    Title: `Meridian Scene ${n}`,
    Code: null,
    Slug: `meridian-scene-${n}`,
    Overview: '',
    Directors: [],
    Credits: [],
    ReleaseDate: '2026-01-01',
    ReleaseDateUtc: '2026-01-01T00:00:00Z',
    // Every generated row carries a cover, so the cover-narrowing parameter has something to act on at every
    // size; a corpus without one would make the narrowed read byte-identical to the plain one and say nothing.
    Images: [{ CoverType: 'Poster', Url: `https://images.example.invalid/covers/meridian-scene-${n}-poster.jpg` }],
    Duration: 300,
    Deleted: false,
    Trailer: null,
    Genres: [],
    Ratings: { Tmdb: null, Imdb: null, Metacritic: null, RottenTomatoes: null, Stash: null },
    Studio: GENERATED_STUDIO,
    Tags: [],
    Collection: null,
    Homepage: null,
    ForeignIds: { TmdbId: 0, StashId: stashId, TpdbId: null },
  };
}

/**
 * The metadata records for {@link count} generated scenes, keyed by the route Whisparr resolves each on —
 * built in memory rather than written to `fixtures/wire-seed/`, because a corpus whose size is the variable
 * under measurement would otherwise leave a hundred committed files no other spec reads.
 */
export function generatedSceneRecordings(count) {
  const recordings = { [`/site/${GENERATED_STUDIO_ID}`]: GENERATED_STUDIO };
  for (let n = 1; n <= count; n++) {
    recordings[`/scene/${generatedSceneId(n)}`] = generatedScene(n);
  }

  return recordings;
}

/**
 * Adds generated scenes `from`..`to` (inclusive, one-based) and returns the set size read back off the
 * instance together with the wall-clock the seeding cost — a figure with no cost attached tells a later
 * reader nothing about what re-taking it would take.
 *
 * @returns {Promise<{ setSize: number, added: number, elapsedMs: number }>}
 */
export async function seedGeneratedScenes(instance, from, to) {
  const roots = await instance.api('GET', '/api/v3/rootfolder');
  const rootFolderPath = roots.json?.[0]?.path ?? ROOT_FOLDER_FALLBACK;
  const profiles = await instance.api('GET', '/api/v3/qualityprofile');
  const qualityProfileId = profiles.json?.[0]?.id ?? 1;

  const started = performance.now();
  for (let n = from; n <= to; n++) {
    const stashId = generatedSceneId(n);
    const res = await instance.api('POST', '/api/v3/movie', {
      foreignId: stashId,
      stashId,
      title: `Meridian Scene ${n}`,
      monitored: n % 2 === 0,
      qualityProfileId,
      rootFolderPath,
      // Loop-safety: nothing this fixture adds may provoke a grab.
      addOptions: { searchForMovie: false },
    });
    if (res.status !== 201) {
      throw new Error(
        `seedGeneratedScenes: adding scene ${n} answered ${res.status}: ` +
          String(res.json?.message ?? res.text).split('\n')[0],
      );
    }
  }

  const elapsedMs = Math.round(performance.now() - started);
  const index = await instance.api('GET', '/api/v3/movie');
  return { setSize: (index.json ?? []).length, added: to - from + 1, elapsedMs };
}
