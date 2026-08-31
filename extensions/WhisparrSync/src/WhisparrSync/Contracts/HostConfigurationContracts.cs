namespace WhisparrSync.Contracts;

/// <summary>
/// What this extension can see, from inside its container, of the host's own configuration, of the
/// host services it can obtain, and of the lifecycle the host gave its background worker.
/// </summary>
/// <remarks>
/// Scalars only, so the response stays the same size however large the library grows. None discloses
/// a filesystem path or any host setting value: the three resolution members say only whether
/// something could be obtained, one member is a count, and the two instants are in this extension's
/// own lifecycle.
/// <para>
/// The two worker instants are here rather than in a log line because only the host can start and
/// cancel that worker, so both halves of its lifecycle have to be readable from outside the process
/// that observed them. The two host-service readings are here for the same reason: the host copies
/// its own scoped and transient registrations into a container it builds per extension, so whether
/// one of them can be obtained is only observable from inside that container.
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
/// <param name="ScanServiceResolved">
/// Whether the host's scan service could be obtained from this extension's container at load. False
/// means nothing this extension does can reach the host's own ingest.
/// </param>
/// <param name="MetadataServerServiceResolved">
/// Whether the host's metadata-server service could be obtained from this extension's container at
/// load. False means provider enrichment is out of reach; it says nothing about the remote identity
/// this extension writes itself, which goes through the database rather than through that service.
/// </param>
public sealed record HostConfigurationView(
    bool ConfigurationResolved,
    int LibraryRootCount,
    DateTimeOffset? WorkerStartedAtUtc,
    DateTimeOffset? WorkerCancelledAtUtc,
    bool ScanServiceResolved,
    bool MetadataServerServiceResolved);
