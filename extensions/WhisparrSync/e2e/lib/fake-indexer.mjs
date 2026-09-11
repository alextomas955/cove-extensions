// A Torznab indexer that answers from its own bytes, so a grab completes with no peer, no tracker
// and no DHT.
//
// It answers Whisparr's `t=caps` and `t=search`/`t=movie-search` with one release derived from the
// query, whose enclosure points back at this container's own `/download.torrent`. That torrent is a
// single-file torrent carrying an HTTP webseed (`url-list`, BEP-19) pointing at `/content/<file>`,
// so the download client fetches the bytes over HTTP and reaches 100% with nothing else running.
//
// WHY A WEBSEED. The alternative is a tracker plus a seeding peer inside a closed Docker network,
// which is three more containers and a DHT that cannot bootstrap there. A webseed is a real torrent
// download through a real torrent engine with none of that.
//
// WHY A REAL VIDEO FILE. The payload has to be a file ffprobe can parse, or the import stalls in
// `importPending` on a header-parsing failure. It also has to be long enough that the instance's
// sample detection does not hold it there with the single word "Sample". A committed fixture is
// copied in; the image has no ffmpeg to synthesize one.
//
// EACH OF THESE WAS A LIVE IMPORT BLOCKER, not an anticipated one:
//   - `pubDate` must be a valid RFC-822 date whose day-of-week MATCHES, or the parser throws "day of
//     week was incorrect" and drops the item. Wed = 2025-01-01.
//   - the release must carry a caps-mapped category (2000/6000), not a bare subcategory, or it is
//     filtered out as "no results in the configured categories".
//   - this generation parses a scene release as "Site - Date - Title", so the title needs that shape.
//   - the indexer must be provisioned with enableInteractiveSearch true; the schema default is false
//     and an interactive search then reports "0 active indexers".
import { GenericContainer, Wait } from "testcontainers";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const IMAGE = process.env.FAKE_INDEXER_IMAGE ?? "node:22-alpine";
const ALIAS = "fake-indexer";
const PORT = 9898;
const MEDIA_IN_CONTAINER = "/fixture/media.mp4";

/**
 * The file this indexer serves over the webseed.
 *
 * Its own fixture rather than the harness's shared one, for two reasons the instance enforced. It
 * has to run long enough that sample detection does not hold the import with the single word
 * "Sample", and it has to carry an audio track or the import is held with "No audio tracks detected".
 * It is still under 100 KB: a black frame at five frames a second, over silence.
 *
 * The command that made it is beside it, in fixtures/media/README.md.
 */
const FIXTURE_MEDIA = join(
  dirname(fileURLToPath(import.meta.url)),
  "../fixtures/media/acquire-media.mp4",
);

/**
 * Starts the Torznab stub on `networkName` under the alias `fake-indexer`.
 *
 * The release title is derived from each search query, so any movie Whisparr searches for gets a
 * matching, grabbable release back.
 *
 * @param {{ networkName: string, site?: string }} options
 * @returns {Promise<{ urlFromWhisparr: string, urlFromHost: string, apiPath: string,
 *   stop: () => Promise<void> }>}
 */
export async function startFakeIndexer({ networkName, site = "Tushy Raw" }) {
  if (!networkName) {
    throw new Error("startFakeIndexer: networkName is required");
  }

  const script = `
const http = require('http');
const crypto = require('crypto');
const { readFileSync } = require('fs');

const ALIAS = ${JSON.stringify(ALIAS)};
const PORT = ${PORT};
const SITE = ${JSON.stringify(site)};
const PIECE_LEN = 32768;
const content = readFileSync(${JSON.stringify(MEDIA_IN_CONTAINER)});
const SIZE = content.length;

function bencode(v) {
  if (Buffer.isBuffer(v)) return Buffer.concat([Buffer.from(v.length + ':'), v]);
  if (typeof v === 'string') { const b = Buffer.from(v, 'utf8'); return Buffer.concat([Buffer.from(b.length + ':'), b]); }
  if (typeof v === 'number') return Buffer.from('i' + Math.trunc(v) + 'e');
  if (Array.isArray(v)) return Buffer.concat([Buffer.from('l'), ...v.map(bencode), Buffer.from('e')]);
  if (v && typeof v === 'object') {
    const keys = Object.keys(v).sort();
    return Buffer.concat([Buffer.from('d'), ...keys.flatMap((k) => [bencode(k), bencode(v[k])]), Buffer.from('e')]);
  }
  throw new Error('bencode: unsupported ' + typeof v);
}
function pieces() {
  const parts = [];
  for (let off = 0; off < SIZE; off += PIECE_LEN) {
    parts.push(crypto.createHash('sha1').update(content.subarray(off, Math.min(off + PIECE_LEN, SIZE))).digest());
  }
  return Buffer.concat(parts);
}
function infoFor(f) { return { name: f, length: SIZE, 'piece length': PIECE_LEN, pieces: pieces() }; }
function torrentBuffer(f, host) {
  return bencode({ announce: 'http://' + host + '/announce', info: infoFor(f), 'url-list': 'http://' + host + '/content/' + encodeURIComponent(f) });
}
function infoHash(f) { return crypto.createHash('sha1').update(bencode(infoFor(f))).digest('hex'); }

function dateFromQuery(q) {
  const yymmdd = (q || '').match(/(\\d{2})[.\\-\\/](\\d{2})[.\\-\\/](\\d{2})(?!\\d)/);
  if (yymmdd) return '20' + yymmdd[1] + '-' + yymmdd[2] + '-' + yymmdd[3];
  const iso = (q || '').match(/(\\d{4})[.\\-\\/](\\d{2})[.\\-\\/](\\d{2})/);
  if (iso) return iso[1] + '-' + iso[2] + '-' + iso[3];
  return '2025-01-01';
}
function titleFromQuery(q) { return SITE + ' - ' + dateFromQuery(q) + ' - Scene XXX 1080p WEBDL'; }
function fileFromTitle(t) { return t.replace(/[^\\w.-]+/g, '.').replace(/\\.+/g, '.') + '.mp4'; }

const CAPS =
  '<?xml version="1.0" encoding="UTF-8"?>' +
  '<caps><server title="FakeIndexer"/><limits max="100" default="50"/>' +
  '<searching><search available="yes" supportedParams="q"/>' +
  '<movie-search available="yes" supportedParams="q,imdbid,tmdbid"/></searching>' +
  '<categories><category id="2000" name="Movies"/><category id="6000" name="XXX"/></categories></caps>';

function rss(host, q) {
  const title = titleFromQuery(q);
  const fname = fileFromTitle(title);
  const dl = 'http://' + host + '/download.torrent?f=' + encodeURIComponent(fname);
  const guid = 'fake-' + infoHash(fname).slice(0, 12);
  return '<?xml version="1.0" encoding="UTF-8"?>' +
    '<rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>' +
    '<item><title>' + title + '</title><guid>' + guid + '</guid>' +
    '<pubDate>Wed, 01 Jan 2025 00:00:00 +0000</pubDate>' +
    '<size>' + SIZE + '</size><link>' + dl + '</link>' +
    '<enclosure url="' + dl + '" length="' + SIZE + '" type="application/x-bittorrent"/>' +
    '<torznab:attr name="category" value="6000"/>' +
    '<torznab:attr name="seeders" value="99"/><torznab:attr name="peers" value="50"/>' +
    '<torznab:attr name="size" value="' + SIZE + '"/></item></channel></rss>';
}

const server = http.createServer((req, res) => {
  const host = req.headers.host || (ALIAS + ':' + PORT);
  const u = new URL(req.url, 'http://' + host);
  if (u.pathname === '/api') {
    const t = u.searchParams.get('t');
    if (t === 'caps') { res.writeHead(200, { 'content-type': 'application/xml' }); return res.end(CAPS); }
    res.writeHead(200, { 'content-type': 'application/rss+xml' }); return res.end(rss(host, u.searchParams.get('q')));
  }
  if (u.pathname === '/download.torrent') {
    res.writeHead(200, { 'content-type': 'application/x-bittorrent' });
    return res.end(torrentBuffer(u.searchParams.get('f') || 'scene.mp4', host));
  }
  if (u.pathname.startsWith('/content/')) {
    res.writeHead(200, { 'content-type': 'video/mp4', 'content-length': SIZE });
    return res.end(content);
  }
  if (u.pathname === '/announce') { res.writeHead(200, { 'content-type': 'text/plain' }); return res.end('d8:intervali1800e5:peers0:e'); }
  res.writeHead(404); res.end('not found');
});
server.listen(PORT, '0.0.0.0', () => console.log('fake-indexer serving ' + SIZE + ' bytes on ' + PORT));
`;

  const container = await new GenericContainer(IMAGE)
    .withNetworkMode(networkName)
    .withNetworkAliases(ALIAS)
    .withExposedPorts(PORT)
    // Copied rather than bind-mounted, so this runs wherever Docker runs. A bind mount depends on the
    // host's own file-sharing configuration, which is why nothing else in this harness uses one.
    .withCopyFilesToContainer([{ source: FIXTURE_MEDIA, target: MEDIA_IN_CONTAINER }])
    .withCommand(["node", "-e", script])
    .withWaitStrategy(Wait.forListeningPorts())
    .withStartupTimeout(60_000)
    .start();

  return {
    urlFromWhisparr: `http://${ALIAS}:${PORT}`,
    urlFromHost: `http://${container.getHost()}:${container.getMappedPort(PORT)}`,
    apiPath: "/api",
    stop: () => container.stop(),
  };
}
