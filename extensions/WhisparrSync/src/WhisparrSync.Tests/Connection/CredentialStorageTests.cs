using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Connection;

// The table here is created by the shipped migration string and mapped by the shipped model
// configuration, so a change to either reaches these tests. They do not prove the host applies
// the migration against its own database.
public sealed class CredentialStorageTests
{
    // The spellings the generation column holds, transcribed from the column rather than computed.
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

    // The pair an outbound request is built from. Held in one row so a reader cannot take the
    // address from one store and the key from another and observe one from either side of a save
    // that moved both: that pairing would post the new key to the instance the old address named.
    [Fact]
    public async Task AWriteStoresTheAddressBesideTheKey()
    {
        await using var database = await CredentialDatabase.CreateAsync();

        await database.ApplyAsync(
            WhisparrGeneration.V3, CredentialWrite.Replace("key-b"), FirstWriteAt, "http://b:6969");

        Assert.Equal(
            new WhisparrStoredConnection("http://b:6969", "key-b"),
            await database.ReadConnectionAsync(WhisparrGeneration.V3));
    }

    // The case the two-store arrangement could not express. A save that moves the instance and
    // leaves the key alone has to move the address this row names, or every later request pairs the
    // stored key with the instance before the move.
    [Fact]
    public async Task AnAddressMovesEvenWhereTheKeyIsKept()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(
            WhisparrGeneration.V3, CredentialWrite.Replace("key-a"), FirstWriteAt, "http://a:6969");

        await database.ApplyAsync(
            WhisparrGeneration.V3, CredentialWrite.Keep, SecondWriteAt, "http://b:6969");

        Assert.Equal(
            new WhisparrStoredConnection("http://b:6969", "key-a"),
            await database.ReadConnectionAsync(WhisparrGeneration.V3));
    }

    // An append would leave two keys for one instance with nothing to say which is current.
    [Fact]
    public async Task ASecondWriteForTheSameGenerationLeavesExactlyOneRow()
    {
        await using var database = await CredentialDatabase.CreateAsync();

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("first-key"), FirstWriteAt);
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("second-key"), SecondWriteAt);

        Assert.Equal(1, await database.CountRowsAsync(V3Stored));
        Assert.Equal("second-key", await database.ReadAsync(WhisparrGeneration.V3));
    }

    // The row is keyed on the generation, so the key's value never decides how many rows exist.
    [Fact]
    public async Task AWriteIdenticalToTheStoredKeyLeavesExactlyOneRow()
    {
        await using var database = await CredentialDatabase.CreateAsync();

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("same-key"), FirstWriteAt);
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("same-key"), SecondWriteAt);

        Assert.Equal(1, await database.CountRowsAsync(V3Stored));
    }

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

    [Fact]
    public async Task ReplacingOneGenerationLeavesTheOtherUntouched()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("v3-key"), FirstWriteAt);
        await database.ApplyAsync(WhisparrGeneration.V2, CredentialWrite.Replace("v2-key"), FirstWriteAt);

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("v3-replaced"), SecondWriteAt);

        Assert.Equal("v2-key", await database.ReadAsync(WhisparrGeneration.V2));
    }

    // The settings form never receives the key back, so a blank field is the ordinary state of a
    // form saving something else. It must keep the stored key.
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

    // A clear and a blank save are separate instructions. Only the clear destroys a stored key.
    [Fact]
    public async Task AnExplicitClearRemovesTheRow()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("doomed-key"), FirstWriteAt);

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Clear, SecondWriteAt);

        Assert.Null(await database.ReadAsync(WhisparrGeneration.V3));
        Assert.Equal(0, await database.CountRowsAsync(V3Stored));
    }

    [Fact]
    public async Task AClearWithNothingStoredIsHarmless()
    {
        await using var database = await CredentialDatabase.CreateAsync();

        await database.ApplyAsync(WhisparrGeneration.V2, CredentialWrite.Clear, FirstWriteAt);

        Assert.Null(await database.ReadAsync(WhisparrGeneration.V2));
        Assert.Equal(0, await database.CountRowsAsync(V2Stored));
    }

    // A borrowed key would authenticate against an instance the user never named.
    [Fact]
    public async Task AGenerationNeverWrittenReadsAsAbsent()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("v3-key"), FirstWriteAt);

        Assert.Null(await database.ReadAsync(WhisparrGeneration.V2));
    }

    [Fact]
    public async Task AReplacementRecordsTheInstantItWasWritten()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("first-key"), FirstWriteAt);

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("second-key"), SecondWriteAt);

        Assert.Equal(SecondWriteAt.UtcTicks, await database.UpdatedAtUtcTicksAsync(V3Stored));
    }

    // The spellings are persisted data. Changing them leaves every existing install's key stored
    // under a name nothing looks up.
    [Fact]
    public async Task TheStoredGenerationSpellingsAreTheOnesTheColumnHolds()
    {
        await using var database = await CredentialDatabase.CreateAsync();

        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("v3-key"), FirstWriteAt);
        await database.ApplyAsync(WhisparrGeneration.V2, CredentialWrite.Replace("v2-key"), FirstWriteAt);

        Assert.Equal([V2Stored, V3Stored], await database.StoredGenerationsAsync());
    }

    // This covers the scope wiring only. The elevation needs a host principal accessor, which no
    // unit test here supplies.
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

    // The table can outlive its receipt; a restored database carries the table with no receipt at
    // all. A second application must not fail.
    [Fact]
    public async Task TheMigrationIsCreateIfAbsent()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        await database.ApplyAsync(WhisparrGeneration.V3, CredentialWrite.Replace("surviving-key"), FirstWriteAt);

        await database.ApplyMigrationAsync();

        Assert.Equal("surviving-key", await database.ReadAsync(WhisparrGeneration.V3));
    }

    [Fact]
    public void AReplacementWithNoKeyIsRefused()
        => Assert.Throws<ArgumentException>(() => CredentialWrite.Replace("   "));

    // The connection stays open for the fixture's lifetime, because an in-memory SQLite database
    // is discarded when its last connection closes. Each operation takes a context of its own, so
    // a read never answers out of the tracker of the write before it.
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

        /// <summary>Re-applies the create-if-absent statement.</summary>
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

        public async Task ApplyAsync(
            WhisparrGeneration generation,
            CredentialWrite write,
            DateTimeOffset nowUtc,
            string address = "")
        {
            await using var context = NewContext();
            await new CredentialPort(context)
                .ApplyAsync(generation, write, address, nowUtc, TestContext.Current.CancellationToken);
        }

        public async Task<WhisparrStoredConnection?> ReadConnectionAsync(WhisparrGeneration generation)
        {
            await using var context = NewContext();
            return await new CredentialPort(context)
                .ReadConnectionAsync(generation, TestContext.Current.CancellationToken);
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

    private sealed class CredentialContext(DbContextOptions<CredentialContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => WhisparrSyncFixture.Create().ConfigureModel(modelBuilder);
    }
}
