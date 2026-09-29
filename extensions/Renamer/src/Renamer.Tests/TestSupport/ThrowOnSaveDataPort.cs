using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

internal sealed class ThrowOnSaveDataPort(DbContext db) : CoveRenamerDataPort(db)
{
    public override Task<string> ApplyAndSaveAsync(RenamerFileMutation mutation, CancellationToken ct = default)
        => throw new InvalidOperationException("forced save failure");
}
