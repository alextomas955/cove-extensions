using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// A database carrying the undo journal and NOTHING else: the extension can complete the load-time
/// check that reads the journal, and every read of a library table throws.
/// </summary>
/// <remarks>
/// This is what "the library read fails" looks like now that the extension refuses to load without a
/// readable journal. Omitting the database entirely used to express the same premise, but it no
/// longer can — a host always registers one, so a test built on its absence would be asserting
/// against a shape that cannot occur.
/// <para>
/// The journal is created by the SHIPPED migration string rather than by the entity model, so this
/// fixture is wrong in the same way production would be wrong, rather than in its own way.
/// </para>
/// </remarks>
internal sealed class JournalOnlyDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _conn;

    private JournalOnlyDatabase(SqliteConnection conn) => _conn = conn;

    public static async Task<JournalOnlyDatabase> CreateAsync()
    {
        var (db, conn) = CoveContextFactory.CreateSqliteContextWithoutSchema();
        await using (db)
        {
            await db.Database.ExecuteSqlRawAsync(RevertJournalSchema.Migration001UpSql);
        }

        return new JournalOnlyDatabase(conn);
    }

    public ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => NewContext());
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        return services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => _conn.DisposeAsync();

    private CoveContext NewContext() =>
        new(new DbContextOptionsBuilder<CoveContext>().UseSqlite(_conn).Options, principalAccessor: null);
}
