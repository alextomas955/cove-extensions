using WhisparrSync.Contracts;

namespace WhisparrSync.Import;

/// <summary>
/// The library roots a Whisparr instance declares for itself.
/// </summary>
/// <remarks>
/// They are not on the event. Both generations' import deliveries were captured verbatim and neither
/// names a root folder under any spelling, so resolving a reported file path against the host's
/// library needs them read from the instance instead.
/// <para>
/// Answers from a shared reading rather than asking per call. A delivery arrives per FILE, and a
/// root list that grows an outbound request per file is a per-file cost on an input whose size is
/// the library's.
/// </para>
/// </remarks>
public interface IReportedRootPort
{
    /// <summary>
    /// The roots <paramref name="generation"/>'s configured instance declares, or an empty list when
    /// it declares none and when there is no configured instance to ask.
    /// </summary>
    /// <remarks>
    /// Empty rather than throwing on an unreachable or unconfigured instance: the caller refuses the
    /// ingest either way, and a refusal it can name beats an exception it has to classify.
    /// </remarks>
    Task<IReadOnlyList<string>> ReadAsync(WhisparrGeneration generation, CancellationToken ct);
}
