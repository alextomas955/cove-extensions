namespace WhisparrSync.Contracts;

/// <summary>
/// What this extension can see of the host's own configuration from inside its container.
/// </summary>
/// <remarks>
/// Two scalars, so the response stays the same size however large the library grows. Neither
/// discloses a filesystem path or any host setting value: the first says only whether the
/// configuration object resolved, and the second is a count.
/// </remarks>
/// <param name="ConfigurationResolved">
/// Whether the host configuration resolved out of this extension's service provider at load.
/// </param>
/// <param name="LibraryRootCount">
/// How many library paths the host has configured, or zero when the configuration did not resolve.
/// </param>
public sealed record HostConfigurationView(bool ConfigurationResolved, int LibraryRootCount);
