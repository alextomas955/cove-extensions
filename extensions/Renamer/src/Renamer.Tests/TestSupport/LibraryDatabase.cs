using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// A throwaway SQLite-backed Cove database and the DI provider an extension loads against it, for the
/// suites whose subject is the LOAD rather than any one query.
/// </summary>
/// <remarks>
/// Its contexts are SCOPED, not a shared singleton, because the paths under test open scopes of their
/// own; the connection is what keeps the one in-memory database alive across them. The journal tables
/// come from <c>EnsureCreatedAsync</c> under the run-wide data-extension registration, which is the
/// host's own behaviour rather than a fixture-only shortcut.
/// </remarks>
internal sealed class LibraryDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _conn;

    private LibraryDatabase(SqliteConnection conn) => _conn = conn;

    public static async Task<LibraryDatabase> CreateAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var library = new LibraryDatabase(conn);
        await using var seed = library.NewContext();
        await seed.Database.EnsureCreatedAsync();
        return library;
    }

    public CoveContext NewContext() =>
        new(new DbContextOptionsBuilder<CoveContext>().UseSqlite(_conn).Options, principalAccessor: null);

    public ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => NewContext());
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        return services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => _conn.DisposeAsync();
}
