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
/// <see cref="CommandsExecuted"/>, never on a row count.
/// </remarks>
public sealed class Library : IAsyncDisposable
{
    private readonly SqliteConnection _conn;

    private Library(SqliteConnection conn) => _conn = conn;

    public FakePrincipalAccessor Principals { get; } = new();

    /// <summary>One executed command: the principal in effect when it ran, and the statement itself.</summary>
    /// <param name="Principal">The principal kind at the command, or null when none was set.</param>
    /// <param name="Sql">
    /// The statement text, which is what tells one scope's read from another's — a body may hold several
    /// scopes with different elevation, so "which principal" is only half the observation.
    /// </param>
    public readonly record struct ExecutedCommand(PrincipalKind? Principal, string Sql);

    /// <summary>Every command the contexts executed, oldest first.</summary>
    /// <remarks>
    /// The principal and the statement are recorded as ONE value rather than as two parallel lists,
    /// because the pair is the observation: two lists can be cleared or read apart, and a verdict about
    /// which principal ran which read would then be assembled from two facts that can disagree.
    /// </remarks>
    public List<ExecutedCommand> CommandsExecuted { get; } = [];

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
                .AddInterceptors(new PrincipalRecorder(Principals, CommandsExecuted))
                .Options,
            Principals);

    public async Task SeedAsync(string[] tags, string[] performers)
    {
        await using var db = NewContext();
        db.Set<Tag>().AddRange(tags.Select(name => new Tag { Name = name }));
        db.Set<Performer>().AddRange(performers.Select(name => new Performer { Name = name }));
        await db.SaveChangesAsync();
    }

    public ServiceProvider BuildProvider(
        ILogger<global::Renamer.Renamer>? log = null, params string[] libraryRoots)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentPrincipalAccessor>(Principals);
        services.AddScoped<DbContext>(_ => NewContext());
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        if (log is not null)
        {
            services.AddSingleton(log);
        }

        // Registered only when a caller names one — see LibraryConfig for the whole statement.
        if (libraryRoots.Length > 0)
        {
            services.AddSingleton(LibraryConfig(libraryRoots));
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The Cove configuration declaring <paramref name="roots"/> as the host's library paths — the list
    /// a destination root is chosen from and re-checked against.
    /// </summary>
    /// <remarks>
    /// THE CANONICAL STATEMENT of why a caller registers this only when it is handed a root; the other
    /// sites that build a provider point here. With no <c>CoveConfiguration</c> registered at all the
    /// extension takes the same path a host that never registered one takes, which is the state one
    /// suite asserts — so a caller handed no root must register nothing rather than an empty list. And a
    /// fixture that names a destination root but forgets to declare it here gets a stated
    /// <c>SkipRootMissing</c> rather than a plausible destination the harness invented on its behalf.
    /// </remarks>
    internal static Cove.Core.Interfaces.CoveConfiguration LibraryConfig(params string[] roots) => new()
    {
        CovePaths = [.. roots.Select(r => new Cove.Core.Interfaces.CovePath { Path = r })],
    };

    public ValueTask DisposeAsync() => _conn.DisposeAsync();

    private sealed class PrincipalRecorder(ICurrentPrincipalAccessor principals, List<ExecutedCommand> sink)
        : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            sink.Add(new ExecutedCommand(principals.Current?.Kind, command.CommandText));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        // A bulk ExecuteDelete/ExecuteUpdate carries no reader, so without this override a statement that
        // ran under the wrong principal would leave no trace at all — the observation would be silently
        // incomplete for exactly the writes a background body performs.
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            sink.Add(new ExecutedCommand(principals.Current?.Kind, command.CommandText));
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
