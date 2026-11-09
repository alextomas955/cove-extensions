using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

// Commits the save for real and then reports a path the file is not at, which is what trips the
// post-save assertion. Only the first save misreports, so the restore that follows reports truthfully
// and the row's final location is its own.
internal sealed class MisreportingSaveDataPort(DbContext db) : CoveRenamerDataPort(db)
{
    private int _saves;

    public override async Task<string> ApplyAndSaveAsync(
        RenamerFileMutation mutation, CancellationToken ct = default)
    {
        string recomputed = await base.ApplyAndSaveAsync(mutation, ct);
        return ++_saves == 1 ? recomputed + ".WRONG" : recomputed;
    }
}
