using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Import;

namespace WhisparrSync.Addressing;

/// <inheritdoc cref="ISampleFilePort"/>
/// <remarks>
/// Binds the base <see cref="DbContext"/> for the same reason the folder source does: this extension
/// compiles against the host's entity assembly but not against the assembly its context lives in,
/// and the host registers that context resolvable as the base type.
/// <para>
/// The narrowing is on the denormalized path column, which the host stores and indexes, so the
/// database answers the prefix from that index rather than by walking the folder rows. The ordering
/// and the limit are the database's for the same reason. Nothing in this file may collect.
/// </para>
/// </remarks>
internal sealed class SampleFilePort(DbContext db) : ISampleFilePort
{
    public async Task<SampleFile?> ReadSampleFileAsync(string coveRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coveRoot);

        // The stored path is the forward-slash form, so a root configured with the other separator
        // would otherwise match nothing. The separator is part of the prefix, so a sibling whose name
        // begins with the root's own is not under it.
        var prefix = PathCandidateGuard.Normalize(coveRoot).TrimEnd('/') + "/";

        return await db.Set<VideoFile>()
            .AsNoTracking()
            .Where(file => file.Path.StartsWith(prefix))
            .OrderBy(file => file.Path)
            .Select(file => new SampleFile(file.Path, file.Size))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}
