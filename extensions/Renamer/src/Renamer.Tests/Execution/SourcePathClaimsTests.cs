using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

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

    [Fact]
    public async Task TwoRowsDifferingOnlyInCase_FollowThePlatformRule()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await ExecutorTestSeed.SeedVideoAsync(db, "media", "clip.mkv", "Lower");
            await ExecutorTestSeed.SeedVideoAsync(db, "Media", "Clip.mkv", "Upper");

            // Observed, not read off PathOps: production picks its comparison from the same flag, so a
            // flag that answered wrongly for this host would rewrite the expectation to match it.
            File.WriteAllText(Path.Combine(dir.Root, "probe.mkv"), "probe");
            bool caseInsensitiveVolume = File.Exists(Path.Combine(dir.Root, "PROBE.mkv"));

            var claims = await new CoveRenamerDataPort(db)
                .CountSourcePathClaimsAsync(["media/clip.mkv"]);

            if (caseInsensitiveVolume)
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
