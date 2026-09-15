namespace WhisparrSync.Addressing;

/// <summary>One file the library holds, and how long it is.</summary>
/// <param name="Path">Its stored path, in the forward-slash spelling the library keeps.</param>
/// <param name="Size">Its length in bytes.</param>
public sealed record SampleFile(string Path, long Size);

/// <summary>Supplies the one file a library root establishes its agreement from.</summary>
/// <remarks>
/// One row per root whatever that root holds. A library reaches millions of files, so the narrowing,
/// the ordering and the limit are all the database's and nothing reading through this collects.
/// <para>
/// The same root answers the same file every time, so a run's probes are repeatable and a held
/// reading describes the same evidence it was taken from.
/// </para>
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
