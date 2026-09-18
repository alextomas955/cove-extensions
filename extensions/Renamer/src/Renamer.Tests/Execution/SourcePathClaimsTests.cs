using Renamer.Execution;

namespace Renamer.Tests.Execution;

/// <summary>
/// <see cref="CoveRenamerDataPort.CountSourcePathClaimsAsync"/> against a real database: which paths
/// it reports as named by more than one file row.
/// </summary>
/// <remarks>
/// The rename refuses every row of a contested path, so a twin this query misses is a file moved out
/// from under the row that still names it. The query is the only thing that sees a twin outside the
/// page being planned, which is why it is exercised here rather than through the fake.
/// <para>
/// Cove keys files uniquely on (ParentFolderId, Basename) and folders uniquely on Path, so two rows
/// cannot hold the same path string. A case variant is the form the anomaly actually takes: two
/// folder rows that are two names for one directory on a case-insensitive volume.
/// </para>
/// </remarks>
public sealed class SourcePathClaimsTests
{
    [Fact]
    public async Task APathNamedOnce_IsAbsent()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await ExecutorTestSeed.SeedVideoAsync(db, "media", "clip.mkv", "Only");

            var claims = await new CoveRenamerDataPort(db)
                .CountSourcePathClaimsAsync(["media/clip.mkv"]);

            Assert.Empty(claims);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Two rows differing only in case follow the platform's rule, not the database collation's.
    /// </summary>
    /// <remarks>
    /// On Windows and macOS they name one physical file, so both are claimants and the rename must
    /// refuse them; the collation's own equality is case-sensitive and would report neither. Elsewhere
    /// they are two files and neither is contested. Only one spelling is passed, because one spelling
    /// is what a planner walking a page holds.
    /// </remarks>
    [Fact]
    public async Task TwoRowsDifferingOnlyInCase_FollowThePlatformRule()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await ExecutorTestSeed.SeedVideoAsync(db, "media", "clip.mkv", "Lower");
            await ExecutorTestSeed.SeedVideoAsync(db, "Media", "Clip.mkv", "Upper");

            var claims = await new CoveRenamerDataPort(db)
                .CountSourcePathClaimsAsync(["media/clip.mkv"]);

            if (PathOps.PathsIgnoreCase)
            {
                Assert.Equal(2, claims["media/clip.mkv"]);
            }
            else
            {
                Assert.Empty(claims);
            }
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>A case variant of a path that was never asked about is not reported.</summary>
    [Fact]
    public async Task AnUnaskedPath_IsAbsentWhateverItsCase()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await ExecutorTestSeed.SeedVideoAsync(db, "media", "clip.mkv", "Lower");
            await ExecutorTestSeed.SeedVideoAsync(db, "Media", "Clip.mkv", "Upper");

            var claims = await new CoveRenamerDataPort(db)
                .CountSourcePathClaimsAsync(["media/other.mkv"]);

            Assert.Empty(claims);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
