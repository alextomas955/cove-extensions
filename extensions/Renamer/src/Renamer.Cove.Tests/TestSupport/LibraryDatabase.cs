using System.Data.Common;
using Cove.Core.Auth;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// A throwaway SQLite-backed Cove database, the DI provider an extension loads against it, the principal
/// its contexts read, and a record of which principal was in effect for each SQL command they ran.
/// </summary>
/// <remarks>
/// Its contexts are SCOPED, not a shared singleton, because the paths under test open scopes of their
/// own; the connection is what keeps the one in-memory database alive across them. The journal tables
/// come from <c>EnsureCreatedAsync</c> under the run-wide data-extension registration, which is the
/// host's own behaviour rather than a fixture-only shortcut.
/// <para>
/// <see cref="CommandsExecuted"/> is the only seam in the suite that can observe the principal a
/// background read actually executes under, which is what makes an elevation claim checkable at all.
/// Under SQLite the row-level consequence of getting elevation wrong cannot be reproduced -
/// <see cref="CoveContext"/> installs its authorization filters only under Npgsql - so the proof
/// available at this tier is the principal AT THE COMMAND, which is the fact those filters consult.
/// Assert on <see cref="CommandsExecuted"/>, never on a row count.
/// </para>
/// </remarks>
internal sealed class LibraryDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _conn;

    private LibraryDatabase(SqliteConnection conn) => _conn = conn;

    public FakePrincipalAccessor Principals { get; } = new();

    /// <summary>One executed command: the principal in effect when it ran, and the statement itself.</summary>
    /// <param name="Principal">The principal kind at the command, or null when none was set.</param>
    /// <param name="Sql">
    /// The statement text, which is what tells one scope's read from another's - a body may hold several
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
        new(
            new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new PrincipalRecorder(Principals, CommandsExecuted))
                .Options,
            Principals);

    /// <summary>
    /// Registered as the extension's logger when set. The host forwards one and the extension falls back
    /// to <c>NullLogger</c>, so a suite whose subject is what the load RECORDED has to supply its own.
    /// </summary>
    public ILogger<global::Renamer.Renamer>? Log { get; set; }

    public ServiceProvider BuildProvider(params string[] libraryPaths)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentPrincipalAccessor>(Principals);
        services.AddScoped<DbContext>(_ => NewContext());
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        services.AddLibraryPaths(libraryPaths);
        if (Log is not null)
        {
            services.AddSingleton(Log);
        }

        return services.BuildServiceProvider();
    }

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
        // ran under the wrong principal would leave no trace at all - the observation would be silently
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
