using Microsoft.EntityFrameworkCore;
using Renamer.Execution;

namespace Renamer.Tests.TestSupport;

internal sealed class CollisionBlindDataPort : CoveRenamerDataPort
{
    public CollisionBlindDataPort(DbContext db) : base(db) { }

    public override Task<bool> CollisionExistsAsync(int folderId, string basename, int selfFileId, CancellationToken ct = default)
        => Task.FromResult(false);
}
