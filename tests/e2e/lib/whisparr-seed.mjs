// Builds the configuration file a Whisparr v2 container must already hold when it starts, because
// that generation takes its API key from nowhere else — no environment spelling reaches it.
//
// Delivered as content into the create→start window rather than through a bind mount, for the same
// reason nothing else in this harness bind-mounts: the host's Docker file-sharing configuration
// never enters into it, so this behaves the same on any contributor's machine and any CI runner.

/**
 * The minimal document the app accepts. It expands this to its full form on its first write, so
 * nothing here anticipates what it will add.
 *
 * `AuthenticationMethod` governs the UI session, NOT the API key: both generations refuse an
 * unauthenticated API read even under `None`.
 *
 * @param {{apiKey: string, port: number}} options
 * @returns {string}
 */
export function buildConfigXml({ apiKey, port }) {
  if (!apiKey) {
    throw new Error(
      "buildConfigXml: no apiKey given; a config carrying none leaves the app minting its own, which no caller can present.",
    );
  }
  if (!port) {
    throw new Error("buildConfigXml: no port given.");
  }
  return `<Config>
  <ApiKey>${apiKey}</ApiKey>
  <AuthenticationMethod>None</AuthenticationMethod>
  <AuthenticationRequired>DisabledForLocalAddresses</AuthenticationRequired>
  <Port>${port}</Port>
  <BindAddress>*</BindAddress>
  <LogLevel>info</LogLevel>
  <AnalyticsEnabled>False</AnalyticsEnabled>
</Config>
`;
}
