namespace WhisparrSync.Addressing;

/// <summary>One file the library holds, and its length in bytes.</summary>
/// <remarks>Path is the forward-slash spelling the library stores.</remarks>
public sealed record SampleFile(string Path, long Size);

/// <summary>Supplies the one file a library root establishes its agreement from.</summary>
/// <remarks>
/// One row per root, whatever that root holds. Libraries reach millions of files, so the narrowing,
/// the ordering and the limit belong to the database and no implementation may collect.
/// <para>A root answers the same file every time, so a run's probes are repeatable.</para>
/// </remarks>
public interface ISampleFilePort
{
    /// <summary>One file under <paramref name="coveRoot"/>, or null where the root holds none.</summary>
    /// <remarks>
    /// A root holding no video file is not a misconfigured root. It has nothing to establish an
    /// agreement from and nothing to offer an instance either, so the absence is an answer.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="coveRoot"/> is blank.</exception>
    Task<SampleFile?> ReadSampleFileAsync(string coveRoot, CancellationToken ct);
}
