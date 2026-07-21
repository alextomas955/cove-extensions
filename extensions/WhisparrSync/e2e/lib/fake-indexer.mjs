// A hermetic Torznab indexer stub + self-hosted content, so a grab completes WITHOUT any real peer,
// tracker, or DHT. It answers Whisparr's Torznab `t=caps` and `t=search`/`t=movie-search` with a
// query-derived release whose enclosure points back at this stub's `/download.torrent`. That torrent is
// a single-file torrent carrying an HTTP **webseed** (`url-list`, BEP-19) pointing at this stub's
// `/content/<file>` — so qBittorrent fetches the bytes over HTTP and reaches 100% with nothing else
// running. The completed file lands in qBit's `whisparr` category dir (shared with Whisparr), Whisparr
// imports it by hardlink.
//
// The served bytes are the harness's real (tiny, valid) fixture MP4 — the payload MUST be a file
// `ffprobe` can parse or Whisparr's DetectSample throws "EBML/…header parsing failed" and import stalls
// in `importPending/warning`. The file is bind-mounted (node:22-alpine has no ffmpeg to synthesize one).
//
// Hard-won Torznab quirks baked in (each was a live import blocker during Phase 33 bring-up):
//   • pubDate MUST be a valid RFC-822 date whose DAY-OF-WEEK matches, or the parser throws
//     "day of week was incorrect" and drops the item ("Wed, 01 Jan 2025" is correct).
//   • the release must carry a caps-mapped movie category (2000/6000), not a bare subcat, or it is
//     filtered out ("no results in the configured categories").
//   • Whisparr Eros parses scene releases as "Site - Date - Title …", so the title needs that prefix.
//   • the indexer must be provisioned with enableInteractiveSearch=true (the schema default is false)
//     or an interactive search reports "0 active indexers" — see setup provisioning.
//   • the fixture's runtime is ~0s, so Whisparr flags it a SAMPLE vs the scene's expected runtime;
//     the harness must disable sample rejection (mediamanagement) for the grab→import assertion.
import { GenericContainer, Wait } from 'testcontainers';

const IMAGE = process.env.FAKE_INDEXER_IMAGE ?? 'node:22-alpine';
const ALIAS = 'fake-indexer';
const PORT = 9898;
const MEDIA_IN_CONTAINER = '/fixture/media.mp4';

/**
 * Starts the Torznab stub joined to {@link networkName} under the alias `fake-indexer`, serving the
 * valid fixture media at {@link mediaHostPath} over the webseed. The release title is derived from each
 * search query (with the Eros "Site - Date -" prefix) so any movie Whisparr searches gets a matching,
 * grabbable release. Returns the URLs a Whisparr indexer config needs + a `stop()`.
 *
 * @param {{ networkName: string, mediaHostPath: string, site?: string }} opts
 * @returns {Promise<{ urlFromWhisparr: string, urlFromHost: string, apiPath: string,
 *   stop: () => Promise<void> }>}
 */
export async function startFakeIndexer({ networkName, mediaHostPath, site = 'Tushy Raw' }) {
  if (!networkName) {
    throw new Error('startFakeIndexer: networkName is required');
  }
  if (!mediaHostPath) {
    throw new Error('startFakeIndexer: mediaHostPath is required (a valid tiny MP4 ffprobe can parse)');
  }

  // The served script — inline (mirrors skyhook-stub.mjs); reads the bind-mounted fixture at startup.
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

// --- minimal bencode encoder (int / string / buffer / list / dict; dict keys sorted per the spec) ---
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
  // 'url-list' is the HTTP webseed qBit downloads from; a dummy announce keeps older clients happy.
  return bencode({ announce: 'http://' + host + '/announce', info: infoFor(f), 'url-list': 'http://' + host + '/content/' + encodeURIComponent(f) });
}
function infoHash(f) { return crypto.createHash('sha1').update(bencode(infoFor(f))).digest('hex'); }

// Whisparr Eros searches scenes by SITE + DATE (q looks like "tushyraw 26.07.05") and matches a release
// by that site+date — so the release DATE must equal the scene's or the import can't map the file to the
// movie. Parse a YY.MM.DD (or YYYY-MM-DD) date out of the query and echo it; fall back to a fixed date.
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
  // pubDate day-of-week MUST be correct (Wed = 2025-01-01) or the RSS parser throws + drops the item.
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
    .withBindMounts([{ source: mediaHostPath, target: MEDIA_IN_CONTAINER, mode: 'ro' }])
    .withCommand(['node', '-e', script])
    .withWaitStrategy(Wait.forListeningPorts())
    .withStartupTimeout(60_000)
    .start();

  const host = container.getHost();
  const port = container.getMappedPort(PORT);

  return {
    urlFromWhisparr: `http://${ALIAS}:${PORT}`,
    urlFromHost: `http://${host}:${port}`,
    apiPath: '/api',
    stop: () => container.stop(),
  };
}

