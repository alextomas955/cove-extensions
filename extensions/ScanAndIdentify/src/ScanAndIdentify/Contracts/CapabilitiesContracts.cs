namespace ScanAndIdentify.Contracts;

/// <summary>What the host supplied this extension at load: its version and which services resolved.</summary>
/// <remarks>
/// <c>CoveVersion</c> is the running host's version, not the floor the manifest declares, and is null
/// until the host has called <c>ConfigureServices</c>. The two flags are false both when a service is
/// unregistered and before <c>InitializeAsync</c> has run.
/// </remarks>
public sealed record CapabilitiesView(string? CoveVersion, bool JobService, bool ScanService);
