using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Connection;

/// <summary>
/// The credential table: at most one row per generation, two generations that never read each
/// other's key, and a save that distinguishes keeping a key from removing one.
/// </summary>
/// <remarks>
/// The table is created by the SHIPPED migration string and mapped by the SHIPPED model
/// configuration, so a change to either reaches these tests. What they cannot prove is that the HOST
/// applies that migration against its own database — that is the end-to-end spec's job.
/// </remarks>
public sealed class CredentialStorageTests
{
    /// <summary>The spellings the generation column holds, transcribed rather than computed.</summary>
    private const string V3Stored = "v3";
    private const string V2Stored = "v2";

    private static readonly DateTimeOffset FirstWriteAt = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondWriteAt = new(2026, 8, 30, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task AWriteThenAReadReturnsTheKey()
    {
        await using var database = await CredentialDatabase.CreateAsync();

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("first-key"), FirstWriteAt);

        Assert.Equal("first-key", await database.ReadAsync(WhisparrGeneration.V3));
    }

    /// <summary>
    /// A second save for the same generation replaces the row it already has. An append would leave
    /// two keys for one instance with nothing to say which is current.
    /// </summary>
    [Fact]
    public async Task ASecondWriteForTheSameGenerationLeavesExactlyOneRow()
    {
        await using var database = await CredentialDatabase.CreateAsync();

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("first-key"), FirstWriteAt);
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("second-key"), SecondWriteAt);

        Assert.Equal(1, await database.CountRowsAsync(V3Stored));
        Assert.Equal("second-key", await database.ReadAsync(WhisparrGeneration.V3));
    }

    /// <summary>
    /// A save whose key is byte-identical to the stored one is still one row, not two. The row is
    /// keyed on the generation, so the key's value can never decide how many rows exist.
    /// </summary>
    [Fact]
    public async Task AWriteIdenticalToTheStoredKeyLeavesExactlyOneRow()
    {
        await using var database = await CredentialDatabase.CreateAsync();

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("same-key"), FirstWriteAt);
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("same-key"), SecondWriteAt);

        Assert.Equal(1, await database.CountRowsAsync(V3Stored));
    }

    /// <summary>Each generation keeps its own key, even when both name the same instance.</summary>
    [Fact]
    public async Task TheTwoGenerationsKeepIndependentRows()
    {
        await using var database = await CredentialDatabase.CreateAsync();

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("v3-key"), FirstWriteAt);
        await database.ApplyAsync(WhisparrGeneration.V2, CredentialWrite.Replace("v2-key"), FirstWriteAt);

        Assert.Equal(1, await database.CountRowsAsync(V3Stored));
        Assert.Equal(1, await database.CountRowsAsync(V2Stored));
        Assert.Equal("v3-key", await database.ReadAsync(WhisparrGeneration.V3));
        Assert.Equal("v2-key", await database.ReadAsync(WhisparrGeneration.V2));
    }

    /// <summary>
    /// Replacing one generation's key leaves the other's alone, so switching generations and coming
    /// back finds the first key where it was.
    /// </summary>
    [Fact]
    public async Task ReplacingOneGenerationLeavesTheOtherUntouched()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("v3-key"), FirstWriteAt);
        await database.ApplyAsync(WhisparrGeneration.V2, CredentialWrite.Replace("v2-key"), FirstWriteAt);

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("v3-replaced"), SecondWriteAt);

        Assert.Equal("v2-key", await database.ReadAsync(WhisparrGeneration.V2));
    }

    /// <summary>
    /// A save carrying no key keeps the stored one. A settings form never receives the key back, so
    /// a blank field is the ordinary state of a form that is saving something else.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankOrAbsentKeyKeepsTheStoredValue(string? submitted)
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("kept-key"), FirstWriteAt);

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.FromSubmitted(submitted), SecondWriteAt);

        Assert.Equal("kept-key", await database.ReadAsync(WhisparrGeneration.V3));
        Assert.Equal(1, await database.CountRowsAsync(V3Stored));
    }

    /// <summary>
    /// An explicit clear removes the row. This is the case a blank save must NOT reach: the two are
    /// separate instructions and only one of them destroys a stored key.
    /// </summary>
    [Fact]
    public async Task AnExplicitClearRemovesTheRow()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("doomed-key"), FirstWriteAt);

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Clear, SecondWriteAt);

        Assert.Null(await database.ReadAsync(WhisparrGeneration.V3));
        Assert.Equal(0, await database.CountRowsAsync(V3Stored));
    }

    /// <summary>A clear against a generation holding nothing changes nothing and raises nothing.</summary>
    [Fact]
    public async Task AClearWithNothingStoredIsHarmless()
    {
        await using var database = await CredentialDatabase.CreateAsync();

        await database.ApplyAsync(WhisparrGeneration.V2, CredentialWrite.Clear, FirstWriteAt);

        Assert.Null(await database.ReadAsync(WhisparrGeneration.V2));
        Assert.Equal(0, await database.CountRowsAsync(V2Stored));
    }

    /// <summary>
    /// A generation nothing has written reads as absent, never as the other generation's key. A
    /// borrowed key would authenticate against an instance the user never named.
    /// </summary>
    [Fact]
    public async Task AGenerationNeverWrittenReadsAsAbsent()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("v3-key"), FirstWriteAt);

        Assert.Null(await database.ReadAsync(WhisparrGeneration.V2));
    }

    /// <summary>The instant travels with the row, so a replacement is distinguishable from the write before it.</summary>
    [Fact]
    public async Task AReplacementRecordsTheInstantItWasWritten()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("first-key"), FirstWriteAt);

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("second-key"), SecondWriteAt);

        Assert.Equal(SecondWriteAt.UtcTicks, await database.UpdatedAtUtcTicksAsync(V3Stored));
    }

    /// <summary>
    /// The generation column holds these two spellings. They are persisted data, so a later change to
    /// them would leave every existing install's key unreadable under a name nothing looks up.
    /// </summary>
    [Fact]
    public async Task TheStoredGenerationSpellingsAreTheOnesTheColumnHolds()
    {
        await using var database = await CredentialDatabase.CreateAsync();

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("v3-key"), FirstWriteAt);
        await database.ApplyAsync(WhisparrGeneration.V2, CredentialWrite.Replace("v2-key"), FirstWriteAt);

        Assert.Equal([V2Stored, V3Stored], await database.StoredGenerationsAsync());
    }

    /// <summary>
    /// A background path reads through a scope of its own. What this proves is the wiring: the
    /// elevation itself needs a host principal accessor, which no unit tier here supplies.
    /// </summary>
    [Fact]
    public async Task AReadThroughTheSystemScopeReturnsTheStoredKey()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("background-key"), FirstWriteAt);

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => database.NewContext());
        await using var provider = services.BuildServiceProvider();

        var read = await CredentialPort.ReadInSystemScopeAsync(
            provider.GetRequiredService<IServiceScopeFactory>(), WhisparrGeneration.V3, TestContext.Current.CancellationToken);

        Assert.Equal("background-key", read);
    }

    /// <summary>
    /// The migration re-runs harmlessly. The table can outlive its receipt — a restored database
    /// carries the table with no receipt at all — so a second application must not fail.
    /// </summary>
    [Fact]
    public async Task TheMigrationIsCreateIfAbsent()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("surviving-key"), FirstWriteAt);

        await database.ApplyMigrationAsync();

        Assert.Equal("surviving-key", await database.ReadAsync(WhisparrGeneration.V3));
    }

    /// <summary>A key of whitespace is not a key. It reaches the keep rule, never the store.</summary>
    [Fact]
    public void AReplacementWithNoKeyIsRefused()
        => Assert.Throws<ArgumentException>(() => CredentialWrite.Replace("   "));

    /// <summary>
    /// A SQLite-in-memory database carrying the credential table and nothing else, created by the
    /// shipped migration string and mapped by the shipped model configuration.
    /// </summary>
    /// <remarks>
    /// A connection held open for the fixture's lifetime, because an in-memory SQLite database is
    /// discarded when its last connection closes. Each operation takes a context of its own, so a
    /// read never answers out of the tracker of the write before it.
    /// </remarks>
    private sealed class CredentialDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private CredentialDatabase(SqliteConnection connection) => _connection = connection;

        public static async Task<CredentialDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var database = new CredentialDatabase(connection);
            await database.ApplyMigrationAsync();
            return database;
        }

        public CredentialContext NewContext()
            => new(new DbContextOptionsBuilder<CredentialContext>().UseSqlite(_connection).Options);

        public async Task ApplyMigrationAsync()
        {
            await using var context = NewContext();
            await context.Database.ExecuteSqlRawAsync(
                WhisparrCredentialSchema.Migration001UpSql, TestContext.Current.CancellationToken);
        }

        public async Task<string?> ReadAsync(WhisparrGeneration generation)
        {
            await using var context = NewContext();
            return await new CredentialPort(context)
                .ReadAsync(generation, TestContext.Current.CancellationToken);
        }

        public async Task ApplyAsync(WhisparrGeneration generation, CredentialWrite write, DateTimeOffset nowUtc)
        {
            await using var context = NewContext();
            await new CredentialPort(context)
                .ApplyAsync(generation, write, nowUtc, TestContext.Current.CancellationToken);
        }

        public async Task<int> CountRowsAsync(string storedGeneration)
        {
            await using var context = NewContext();
            return await context.Set<WhisparrCredentialEntity>()
                .CountAsync(row => row.Generation == storedGeneration, TestContext.Current.CancellationToken);
        }

        public async Task<long> UpdatedAtUtcTicksAsync(string storedGeneration)
        {
            await using var context = NewContext();
            var row = await context.Set<WhisparrCredentialEntity>()
                .SingleAsync(entity => entity.Generation == storedGeneration, TestContext.Current.CancellationToken);
            return row.UpdatedAtUtcTicks;
        }

        public async Task<List<string>> StoredGenerationsAsync()
        {
            await using var context = NewContext();
            return await context.Set<WhisparrCredentialEntity>()
                .Select(row => row.Generation)
                .OrderBy(generation => generation)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    /// <summary>A context carrying the extension's own model configuration and nothing else.</summary>
    private sealed class CredentialContext(DbContextOptions<CredentialContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => WhisparrSyncFixture.Create().ConfigureModel(modelBuilder);
    }
}
