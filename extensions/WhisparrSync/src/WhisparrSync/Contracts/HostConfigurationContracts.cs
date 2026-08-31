namespace WhisparrSync.Contracts;

/// <summary>
/// What this extension can see, from inside its container, of the host's own configuration and of the
/// lifecycle the host gave this extension's background worker.
/// </summary>
/// <remarks>
/// Four scalars, so the response stays the same size however large the library grows. None discloses
/// a filesystem path or any host setting value: the first says only whether the configuration object
/// resolved, the second is a count, and the last two are instants in this extension's own lifecycle.
/// <para>
/// The two worker instants are here rather than in a log line because only the host can start and
/// cancel that worker, so both halves of its lifecycle have to be readable from outside the process
/// that observed them.
/// </para>
/// </remarks>
/// <param name="ConfigurationResolved">
/// Whether the host configuration resolved out of this extension's service provider at load.
/// </param>
/// <param name="LibraryRootCount">
/// How many library paths the host has configured, or zero when the configuration did not resolve.
/// </param>
/// <param name="WorkerStartedAtUtc">
/// When the host last started this extension's background worker, or null when it never has.
/// </param>
/// <param name="WorkerCancelledAtUtc">
/// When that worker's token was last cancelled, or null when it never was. Non-null beside a later
/// <paramref name="WorkerStartedAtUtc"/> is a worker the host stopped and started again.
/// </param>
public sealed record HostConfigurationView(
    bool ConfigurationResolved,
    int LibraryRootCount,
    DateTimeOffset? WorkerStartedAtUtc,
    DateTimeOffset? WorkerCancelledAtUtc);
