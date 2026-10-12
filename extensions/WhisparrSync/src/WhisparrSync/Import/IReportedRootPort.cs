using WhisparrSync.Contracts;

namespace WhisparrSync.Import;

/// <summary>
/// The library roots a Whisparr instance declares for itself.
/// </summary>
/// <remarks>
/// Neither generation's import delivery names a root folder under any spelling, so the roots are
/// read from the instance to resolve a reported path against the host's library.
/// <para>
/// Answers from a shared reading rather than asking per call. A delivery arrives per file, so an
/// outbound request per call would be a per-file cost on an input the size of the library.
/// </para>
/// </remarks>
public interface IReportedRootPort
{
    /// <summary>
    /// The roots <paramref name="generation"/>'s configured instance declares, an empty list where
    /// it declares none, or null where the list could not be established at all.
    /// </summary>
    /// <remarks>
    /// Null rather than throwing on an unreachable or unconfigured instance: the caller refuses
    /// either way, and a named refusal beats an exception it has to classify.
    /// <para>
    /// Null is distinct from empty. Empty is the instance declaring no root; null says nothing was
    /// established, and a caller can draw no containment conclusion from a list nobody read.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<string>?> ReadAsync(WhisparrGeneration generation, CancellationToken ct);
}
