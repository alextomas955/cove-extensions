// The one site the Whisparr fixture images are declared. The fixture module, the smoke and any probe
// all resolve their reference through here, so a version bump is one deliberate edit rather than a
// search — and a bump is what obliges a re-run of the measurements taken against these builds.
//
// A floating tag is refused: `latest` is a v2 image, so a floating reference is free to select the
// wrong GENERATION, which is the one axis these fixtures exist to tell apart.

// The registry host is part of it: a reference missing one resolves to Docker Hub, which is a real
// registry nobody named.
const REPOSITORY = "ghcr.io/hotio/whisparr";

// The release channel only. The same repository also carries develop- and nightly-channel builds,
// which move under a reader.
const TAGS = Object.freeze({
  v3: "v3-3.3.8-release.1097",
  v2: "v2-2.2.0-release.231",
});

/** Complete references, composed from the single repository above so the two cannot drift apart. */
export const WHISPARR_IMAGES = Object.freeze(
  Object.fromEntries(
    Object.entries(TAGS).map(([generation, tag]) => [generation, `${REPOSITORY}:${tag}`]),
  ),
);

/**
 * The complete image reference for a Whisparr generation.
 *
 * Throws rather than answering undefined on an unknown generation: an undefined reference reaches
 * the daemon as a pull of nothing, and the failure that follows names neither the caller nor the
 * typo that caused it.
 *
 * @param {"v3"|"v2"} generation
 * @returns {string}
 */
export function whisparrImage(generation) {
  const image = WHISPARR_IMAGES[generation];
  if (image === undefined) {
    throw new Error(
      `whisparrImage: unknown generation "${generation}"; declared generations are ${Object.keys(WHISPARR_IMAGES).join(", ")}.`,
    );
  }
  return image;
}
