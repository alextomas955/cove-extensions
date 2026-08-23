using Cove.Data;
using Cove.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Journal;

/// <summary>
/// What happens at load when the undo journal is not there: the extension refuses, naming the table
/// and the consequence, instead of loading and renaming files it could never put back.
/// </summary>
/// <remarks>
/// The refusal is the whole point of the case. The host applies an extension's migrations, logs a
/// failure, and loads the extension anyway — so without this check a journal that never got created
/// costs the user every undo, silently. A throw out of the load is caught per extension and disables
/// that one extension, which is a failure nobody can read as success.
/// </remarks>
[Trait("Tier", "L1")]
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class JournalStartupAssertionTests
{
    [Fact]
    public async Task WithTheJournalPresent_TheLoadCompletes()
    {
        await using var library = await Library.CreateAsync();
        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(new FakeStore());

        var ex = await Record.ExceptionAsync(() => ext.InitializeAsync(library.BuildProvider()));
        Assert.Null(ex);
    }

    [Fact]
    public async Task WithTheJournalTableGone_TheLoadRefuses_NamingTheTableAndTheConsequence()
    {
        await using var db = await LibraryWithNoJournalAsync();
        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(new FakeStore());

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ext.InitializeAsync(db.BuildProvider()));

        Assert.Contains("renamer_revert_batches", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("undo", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A database that is valid in every other respect and simply has no journal — the state a
    /// migration that failed and was logged-and-skipped leaves behind.
    /// </summary>
    private static async Task<NoJournalDatabase> LibraryWithNoJournalAsync()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using (db)
        {
            // Dropped rather than never created, because the model this suite runs under contributes
            // the journal to every EnsureCreated — which is exactly the host's own behaviour, and so
            // the only honest way to reach the missing-table state is to take the table away.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE renamer_revert_rows");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE renamer_revert_batches");
        }

        return new NoJournalDatabase(conn);
    }

    private sealed class NoJournalDatabase(SqliteConnection conn) : IAsyncDisposable
    {
        public ServiceProvider BuildProvider()
        {
            var services = new ServiceCollection();
            services.AddScoped<DbContext>(_ =>
                new CoveContext(
                    new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn).Options,
                    principalAccessor: null));
            services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
            return services.BuildServiceProvider();
        }

        public ValueTask DisposeAsync() => conn.DisposeAsync();
    }
}
