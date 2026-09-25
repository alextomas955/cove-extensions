// Settles how provider configuration actually reaches a fixture Cove: whether the environment form
// binds a list of complex objects into Scraping.MetadataServers, or whether Cove's own configuration
// API is the route that works.
//
// The read-back route returns provider API keys in PLAINTEXT to a principal holding
// SystemSettingsWrite, which is what the harness bootstraps. So every entry recorded here is a
// described one, and the response is never persisted whole.
import { judgeBinding } from "../lib/context.mjs";

export const row = {
  id: "cove-provider-binding",
  label: "Does COVE__Scraping__MetadataServers__N__* reach Cove's own configuration?",
  requires: {
    cove: true,
    whisparr: [],
    seedHistory: false,
    support: [],
    network: false,
    live: false,
  },
  async run(ctx) {
    const { source, skip, servers, observedFromEnv, envVarsInContainer, delivery } = ctx.providers;
    const { verdict, mismatches } = judgeBinding(servers, observedFromEnv);

    return {
      method: {
        verb: "GET",
        path: "/api/system/config",
        inputs: { mechanism: "container environment", keys: Object.keys(ctx.providers.env) },
      },
      verdict,
      ...(skip === null ? {} : { skip }),
      observed: {
        providerSource: source,
        // Discriminates a binder that refused the entries from a delivery that never carried them.
        envVarsInContainer,
        configured: servers,
        readBackFromEnv: observedFromEnv,
        mismatches,
        configurationApiFallback: verdict === "bound" ? null : delivery,
      },
    };
  },
};
