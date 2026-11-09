using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

// Misreports the saved path, as MisreportingSaveDataPort does, and re-occupies slot between the save
// and the rollback that follows - so the file cannot get back to it and the caller meets the arm where
// the rollback itself could not complete.
internal sealed class ReoccupySlotThenMisreportDataPort(DbContext db, string slot)
    : CoveRenamerDataPort(db)
{
    public override async Task<string> ApplyAndSaveAsync(
        RenamerFileMutation mutation, CancellationToken ct = default)
    {
        string recomputed = await base.ApplyAndSaveAsync(mutation, ct);
        await File.WriteAllTextAsync(slot, "intruder bytes re-occupying the slot", ct);
        return recomputed + ".WRONG";
    }
}
