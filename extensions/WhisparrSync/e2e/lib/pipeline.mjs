// Provisions a Whisparr instance for the hermetic acquire→import pipeline: a Torznab indexer pointed at
// the fake indexer, a qBittorrent download client, sample-rejection disabled (the fixture media has ~0s
// runtime), and — because the whole stack is on ONE Docker network — the On-Import webhook. Every step
// here mirrors exactly what was validated live during Phase 33 bring-up (see PIPELINE-VALIDATION.md).
//
// All calls go through Whisparr's own /api/v3 (both versions expose the Sonarr-shaped surface). Indexer
// + download-client hosts are the CONTAINER ALIASES (`fake-indexer`, `qbittorrent`), reachable from the
// Whisparr container over the shared network — never host.docker.internal.

/** POSTs a schema-derived config to a Whisparr endpoint, filling named fields. Returns the created row. */
async function addFromSchema(whisparr, resource, implementation, overrides, fieldValues) {
  const headers = { 'X-Api-Key': whisparr.apiKey, 'Content-Type': 'application/json' };
  const schemaRes = await fetch(`${whisparr.baseUrlFromHost}/api/v3/${resource}/schema`, { headers });
  const schemas = schemaRes.ok ? await schemaRes.json() : [];
  const schema = schemas.find((s) => s.implementation === implementation);
  if (!schema) {
    throw new Error(`provisionPipeline: no ${resource} schema for ${implementation}`);
  }
  const body = {
    ...schema,
    ...overrides,
    fields: schema.fields.map((f) => (f.name in fieldValues ? { ...f, value: fieldValues[f.name] } : f)),
  };
  const res = await fetch(`${whisparr.baseUrlFromHost}/api/v3/${resource}?forceSave=true`, {
    method: 'POST',
    headers,
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    throw new Error(`provisionPipeline: add ${implementation} failed HTTP ${res.status}: ${await res.text()}`);
  }
  return res.json();
}

/**
 * Adds the Torznab indexer + qBittorrent download client, disables sample rejection, and (optionally)
 * registers the On-Import webhook via the extension. Returns the created indexer/client ids.
 *
 * @param {{ whisparr, fakeIndexer, qbit, api?, extensionId?: string, registerWebhook?: boolean }} opts
 */
export async function provisionPipeline({ whisparr, fakeIndexer, qbit, api, extensionId, registerWebhook = true }) {
  const headers = { 'X-Api-Key': whisparr.apiKey, 'Content-Type': 'application/json' };

  // Torznab indexer → the fake indexer. enableInteractiveSearch MUST be true (schema default is false →
  // an interactive search finds "0 active indexers"); categories [6000,2000] match the caps mapping so
  // the release is not filtered out ("no results in the configured categories").
  const indexer = await addFromSchema(
    whisparr,
    'indexer',
    'Torznab',
    { name: 'FakeIndexer', enableRss: false, enableInteractiveSearch: true, enableAutomaticSearch: true, priority: 1 },
    { baseUrl: fakeIndexer.urlFromWhisparr, apiPath: fakeIndexer.apiPath ?? '/api', apiKey: '', categories: [6000, 2000] },
  );

  // qBittorrent download client → the qBit container alias. The `whisparr` category routes completed
  // files into the shared download dir Whisparr imports from.
  const client = await addFromSchema(
    whisparr,
    'downloadclient',
    'QBittorrent',
    { name: 'qBittorrent', enable: true, priority: 1 },
    { host: 'qbittorrent', port: 8080, useSsl: false, username: 'admin', password: '', movieCategory: qbit.category ?? 'whisparr' },
  );

  // The fixture media has ~0s runtime, so Whisparr's DetectSample would hold import in importPending;
  // relax the media-management config so a "sample"-length file still imports for the pipeline assertion.
  const mmRes = await fetch(`${whisparr.baseUrlFromHost}/api/v3/config/mediamanagement`, { headers });
  if (mmRes.ok) {
    const mm = await mmRes.json();
    // enableMediaInfo:false skips storing MediaInfo; the DetectSample runtime check is instead skipped by
    // the fixture scene's Duration:0 metadata (Whisparr treats 0-runtime as indeterminate, not a sample).
    await fetch(`${whisparr.baseUrlFromHost}/api/v3/config/mediamanagement`, {
      method: 'PUT',
      headers,
      body: JSON.stringify({ ...mm, enableMediaInfo: false }),
    }).catch(() => {});
  }

  // AcceptableSizeSpecification would reject the tiny fixture file (with an unknown runtime Whisparr
  // assumes a ~110-min median → a multi-GB expected size). Zero every quality definition's min size (and
  // lift the max) so a hermetic sub-MB fixture passes the size gate.
  const qdRes = await fetch(`${whisparr.baseUrlFromHost}/api/v3/qualitydefinition`, { headers });
  if (qdRes.ok) {
    const defs = (await qdRes.json()).map((d) => ({ ...d, minSize: 0, maxSize: null, preferredSize: null }));
    await fetch(`${whisparr.baseUrlFromHost}/api/v3/qualitydefinition/update`, {
      method: 'PUT',
      headers,
      body: JSON.stringify(defs),
    }).catch(() => {});
  }

  let webhookRegistered = false;
  if (registerWebhook && api && extensionId) {
    // On ONE shared network the webhook URL uses the Cove alias, so auto-register succeeds here (unlike
    // the split-network dev env where it returns registered:false).
    const res = await api.post(`/api/extensions/${extensionId}/register-webhook`, {});
    webhookRegistered = Boolean(res.json?.registered);
  }

  return { indexerId: indexer.id, downloadClientId: client.id, webhookRegistered };
}
