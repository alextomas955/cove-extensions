using System.Data.Common;
using Cove.Core.Auth;
using Cove.Core.Events;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Renamer.Tests.TestSupport;

// A throwaway SQLite-backed Cove database and the DI provider an extension loads against it. Its
// contexts are scoped, not a shared singleton, because the paths under test open scopes of their
// own; the connection is what keeps the one in-memory database alive across them. The journal
// tables come from EnsureCreatedAsync under the run-wide data-extension registration, which is the
// host's own behaviour rather than a fixture-only shortcut. Under SQLite the row-level consequence
// of getting elevation wrong cannot be reproduced - CoveContext installs its authorization filters
// only under Npgsql - so the proof available at this tier is the principal at the command, which is
// the fact those filters consult. Assert on CommandsExecuted, never on a row count.
internal sealed class LibraryDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _conn;

    private LibraryDatabase(SqliteConnection conn) => _conn = conn;

    public FakePrincipalAccessor Principals { get; } = new();

    // Registered as one instance for the fixture, where the host registers the service scoped, so
    // the asks a run makes across its scopes are observable in one place. Nothing on these paths
    // reads the service's lifetime.
    public RecordingAuthorizationService Authorization { get; } = new();

    // Principal is null when none was set. Sql is what tells one scope's read from another's: a
    // body may hold several scopes with different elevation, so "which principal" is only half the
    // observation.
    public readonly record struct ExecutedCommand(PrincipalKind? Principal, string Sql);

    // Every command the contexts executed, oldest first.
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

    public DbContext NewContext() =>
        new CoveContext(
            new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new PrincipalRecorder(Principals, CommandsExecuted))
                .Options,
            Principals);

    // Registered as the extension's logger when set. The host forwards one and the extension falls
    // back to NullLogger, so a suite whose subject is what the load recorded has to supply its own.
    public ILogger<Renamer>? Log { get; set; }

    public ServiceProvider BuildProvider(params string[] libraryPaths)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentPrincipalAccessor>(Principals);
        services.AddSingleton<IAuthorizationService>(Authorization);
        services.AddScoped<DbContext>(_ => NewContext());
        services.AddSingleton<IEventBus>(new CapturingEventBus());
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
