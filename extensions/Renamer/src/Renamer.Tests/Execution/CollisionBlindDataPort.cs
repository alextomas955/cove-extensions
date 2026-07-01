using Cove.Data;
using Renamer.Execution;

namespace Renamer.Tests.Execution;

/// <summary>
/// A test-only <see cref="CoveRenamerDataPort"/> that LIES about the DB collision pre-check
/// (<see cref="CollisionExistsAsync"/> always returns false). Used by the adversarial collision/
/// rollback tests to bypass the executor's proactive suffixing so the actual <c>SaveChangesAsync</c> hits the
/// real <c>(ParentFolderId, Basename)</c> unique index and throws — exercising the index BACKSTOP +
/// the disk rollback, not the pre-check happy path. Everything else (load, get-or-create, save)
/// delegates to the real port over the live <see cref="CoveContext"/>.
/// </summary>
internal sealed class CollisionBlindDataPort : CoveRenamerDataPort
{
    public CollisionBlindDataPort(CoveContext db) : base(db) { }

    public override Task<bool> CollisionExistsAsync(int folderId, string basename, int selfFileId, CancellationToken ct = default)
        => Task.FromResult(false);
}
