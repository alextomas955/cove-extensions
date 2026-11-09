using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

// Cancels instead of saving, which is how a host shutdown reaches a save in flight.
internal sealed class CancelOnSaveDataPort(DbContext db) : CoveRenamerDataPort(db)
{
    public override Task<string> ApplyAndSaveAsync(
        RenamerFileMutation mutation, CancellationToken ct = default)
        => throw new OperationCanceledException("host shutting down mid-save");
}
