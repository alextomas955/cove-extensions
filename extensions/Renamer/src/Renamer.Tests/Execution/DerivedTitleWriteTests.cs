using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The title write at the port, against the row rather than against the plan that asked for it.
/// </summary>
/// <remarks>
/// The planner derives a title only for a title-less item, so the port's own emptiness re-check looks
/// redundant from the plan's side - and it is exactly the window it exists for: a person can type a
/// title between the preview and the run, and this is the only place the extension writes metadata
/// rather than location, so a rename must never overwrite what they wrote. The same check is what makes
/// the write idempotent across the files of a multi-file item, which save one at a time.
/// </remarks>
public sealed class DerivedTitleWriteTests
{
    [Fact]
    public async Task ATitleWrite_LandsOnlyOnARowThatIsStillTitleless_AndTheRenamerLandsEitherWay()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            string siblingDir = Path.Combine(dir.Root, "sibling").Replace('\\', '/');

            var (_, titlelessId, titlelessFileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "one.mkv", title: null!);
            var (_, titledId, titledFileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, siblingDir, "two.mkv", "Typed In By Hand");

            var port = new CoveRenamerDataPort(db);
            await port.ApplyAndSaveAsync(
            [
                new RenamerFileMutation(
                    titlelessFileId, "one renamed.mkv", null, null,
                    new RenamerEntityTitleWrite(RenamerFileKind.Video, titlelessId, "one")),
                new RenamerFileMutation(
                    titledFileId, "two renamed.mkv", null, null,
                    new RenamerEntityTitleWrite(RenamerFileKind.Video, titledId, "two")),
            ]);

            // The rename half of both mutations committed, so the refusal below is the title check and
            // not a save that never happened.
            var (titlelessBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, titlelessFileId);
            var (titledBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, titledFileId);
            Assert.Equal("one renamed.mkv", titlelessBasename);
            Assert.Equal("two renamed.mkv", titledBasename);

            Assert.Equal("one", await ExecutorTestSeed.ReadVideoTitleAsync(db, titlelessId));
            Assert.Equal("Typed In By Hand", await ExecutorTestSeed.ReadVideoTitleAsync(db, titledId));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
