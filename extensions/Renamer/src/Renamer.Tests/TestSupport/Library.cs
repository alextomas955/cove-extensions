using System.Data.Common;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// A throwaway SQLite-backed Cove database, the principal its contexts read, and a record of which
/// principal was in effect for each SQL command they ran.
/// </summary>
/// <remarks>
/// This is the only seam in the suite that can observe the principal a background read actually
/// executes under, which is what makes an elevation claim checkable at all. Under SQLite the
/// row-level consequence of getting elevation wrong cannot be reproduced — <c>CoveContext</c>
/// installs its authorization filters only under Npgsql — so the proof available at this tier is the
/// principal AT THE COMMAND, which is the fact those filters consult. Assert on
/// <see cref="PrincipalPerCommand"/>, never on a row count.
/// </remarks>
public sealed class Library : IAsyncDisposable
{
    private readonly SqliteConnection _conn;

    private Library(SqliteConnection conn) => _conn = conn;

    public FakePrincipalAccessor Principals { get; } = new();

    /// <summary>The principal kind in effect at each executed command, oldest first.</summary>
    public List<PrincipalKind?> PrincipalPerCommand { get; } = [];

    public static async Task<Library> CreateAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var library = new Library(conn);
        await using var seed = library.NewContext();
        await seed.Database.EnsureCreatedAsync();
        return library;
    }

    public CoveContext NewContext() =>
        new(
            new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new PrincipalRecorder(Principals, PrincipalPerCommand))
                .Options,
            Principals);

    public async Task SeedAsync(string[] tags, string[] performers)
    {
        await using var db = NewContext();
        db.Set<Tag>().AddRange(tags.Select(name => new Tag { Name = name }));
        db.Set<Performer>().AddRange(performers.Select(name => new Performer { Name = name }));
        await db.SaveChangesAsync();
    }

    public ServiceProvider BuildProvider(ILogger<global::Renamer.Renamer>? log = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentPrincipalAccessor>(Principals);
        services.AddScoped<DbContext>(_ => NewContext());
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        if (log is not null)
        {
            services.AddSingleton(log);
        }

        return services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => _conn.DisposeAsync();

    private sealed class PrincipalRecorder(ICurrentPrincipalAccessor principals, List<PrincipalKind?> sink)
        : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            sink.Add(principals.Current?.Kind);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
