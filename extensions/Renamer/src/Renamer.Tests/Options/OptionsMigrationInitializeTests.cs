using System.Data.Common;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Options;

/// <summary>
/// The impure half of the one-time name→id options conversion: the initialize-time seam that reads the
/// library, refuses to write unless that read came back non-empty, and stamps itself once.
/// </summary>
/// <remarks>
/// Driven against a real SQLite-backed <see cref="CoveContext"/> and a real
/// <see cref="ICurrentPrincipalAccessor"/> rather than a fake data port, because the failure this seam
/// exists to prevent lives in the port: under an unelevated principal Cove's authorization query filters
/// return zero rows SUCCESSFULLY, so a fake that simply hands rows back could never reproduce it.
/// <para>
/// One bound is measured and stated in its own test below: Cove configures those filters only under
/// Npgsql, so this SQLite tier can prove WHICH PRINCIPAL each read executes under, but not the
/// row-level consequence of getting it wrong.
/// </para>
/// <para>
/// Every store fixture pre-stamps the undo journal's schema, so a <c>SetCallCount</c> claim here is
/// about the options conversion alone and not about the journal discard that shares
/// <c>InitializeAsync</c> — the same isolation <c>ScanLibraryEndpointTests</c> applies to its own
/// wrote-nothing proof.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
public sealed class OptionsMigrationInitializeTests
{
    /// <summary>Hand-written in the shape a pre-migration install stored, not generated from the converter.</summary>
    private const string LegacyBlob =
        """
        {"FilenameTemplate":"$title","TagDestinations":{"Anime":"/media/anime"},"ExcludeTags":["Sample"]}
        """;

    /// <summary>
    /// What the pre-migration panel ACTUALLY wrote for a user who configured one tag rule and nothing
    /// else: every legacy key present, most of them empty, because the panel serialized its whole
    /// defaults object. Transcribed by hand from that shipped default, never generated here.
    /// </summary>
    private const string RealisticLegacyBlob =
        """
        {
          "FilenameTemplate": "$title",
          "Performers": { "Separator": " ", "Whitelist": [], "Blacklist": [] },
          "Tags": { "Separator": " ", "Whitelist": [], "Blacklist": [] },
          "TagDestinations": { "Anime": "/media/anime" },
          "ExcludeTags": []
        }
        """;

    // ── The two refusal paths ─────────────────────────────────────────────────

    [Fact]
    public async Task WhenTheLibraryReadThrows_WritesNothing_AndTheLegacyBlobIsUntouched()
    {
        var store = await NewLegacyStoreAsync();
        int setsBefore = store.SetCallCount;

        // No DbContext registration at all, so resolving one throws — the failure path.
        var services = new ServiceCollection();
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        await InitializeAsync(store, services.BuildServiceProvider());

        Assert.Equal(setsBefore, store.SetCallCount);
        await AssertStillLegacyAsync(store);
    }

    [Fact]
    public async Task WhenTheLibraryReadReturnsZeroRows_WritesNothing_AndTheLegacyBlobIsUntouched()
    {
        // THE case that matters. A read that comes back empty is indistinguishable from a read the
        // authorization filters silently emptied, and the conversion keeps no copy of the originals — so
        // writing here would erase the user's entire tag configuration. A suite covering only the
        // throwing case above passes while this path stays wide open.
        var store = await NewLegacyStoreAsync();
        int setsBefore = store.SetCallCount;

        await using var library = await Library.CreateAsync();

        // Schema present, tag and performer tables empty.
        await InitializeAsync(store, library.BuildProvider());

        Assert.Equal(setsBefore, store.SetCallCount);
        await AssertStillLegacyAsync(store);
    }

    [Fact]
    public async Task ALibraryWithTagsButNoPerformers_StillConverts_BecauseTheEmptyPerformerKeysNeedNoRows()
    {
        // A library with tags and no performer rows is ordinary, not degenerate — and the blob above is
        // what such a user has, because the panel wrote an empty Whitelist/Blacklist for BOTH groups
        // whether or not they configured either. Gating on key PRESENCE therefore demands performer rows
        // that legitimately do not exist, defers on every start, and never converts; the tag rule then
        // keeps its name key, the blob keeps failing to bind, and every rename silently runs the default
        // template. Gating on names that actually need resolving is what makes that reachable state
        // convert instead.
        var store = await NewStoreAsync();
        await new OptionsStore(store).SaveRawAsync(RealisticLegacyBlob);

        await using var library = await Library.CreateAsync();
        await library.SeedAsync(tags: ["Anime"], performers: []);

        await InitializeAsync(store, library.BuildProvider());

        var converted = await LoadOptionsAsync(store);
        Assert.Equal(["/media/anime"], converted.TagDestinations.Values);
        Assert.Equal(OptionsMigration.CurrentSchema, await store.GetAsync(OptionsMigration.SchemaKey));
    }

    // ── The elevation the zero-row rule stands beside ─────────────────────────

    [Fact]
    public async Task TheConversionsRead_ExecutesUnderSystem_AndLeavesTheCallersPrincipalBehindIt()
    {
        // Anonymous is what a detached background flow carries under auth-on, and under Postgres its
        // authorization filters would empty this read with no error. The claim is pinned at the SQL
        // command itself — the principal in effect when the reader actually executes — because that is
        // the fact the filters consult, and it stays true whichever provider is underneath.
        var store = await NewLegacyStoreAsync();
        await using var library = await Library.CreateAsync();
        await library.SeedAsync(tags: ["Anime", "Sample"], performers: []);
        library.Principals.Set(CovePrincipal.Anonymous());
        library.PrincipalPerCommand.Clear();

        await InitializeAsync(store, library.BuildProvider());

        Assert.NotEmpty(library.PrincipalPerCommand);
        Assert.All(library.PrincipalPerCommand, kind => Assert.Equal(PrincipalKind.System, kind));

        // Elevation is a span, not a mode: the helper put the caller's principal back.
        Assert.Equal(PrincipalKind.Anonymous, library.Principals.Current!.Kind);

        var converted = await LoadOptionsAsync(store);
        Assert.Equal(["/media/anime"], converted.TagDestinations.Values);
        Assert.Single(converted.ExcludeTagIds);
        Assert.Equal("$title", converted.FilenameTemplate);
    }

    [Fact]
    public async Task OnSqlite_AnAnonymousReadIsUnfiltered_WhichIsWhatBoundsThisTiersProof()
    {
        // Measured, and recorded because it decides what the test above is allowed to claim:
        // CoveContext installs ConfigureAuthorizationFilters ONLY under Npgsql, so the zero-row
        // consequence of an unelevated read cannot be reproduced here at all. That is why the elevation
        // is proven by the principal at the command rather than by a row count, and why the row-level
        // behaviour needs a Postgres-backed tier. If Cove ever configures the filters for every
        // provider, this test goes red and the stronger row-count proof becomes available.
        await using var library = await Library.CreateAsync();
        await library.SeedAsync(tags: ["Anime"], performers: []);
        library.Principals.Set(CovePrincipal.Anonymous());

        await using var db = library.NewContext();
        Assert.Single(await db.Set<Tag>().AsNoTracking().ToListAsync());
    }

    // ── One-time-ness ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ASecondInitializeAgainstAnAlreadyStampedStore_WritesNothing()
    {
        var store = await NewLegacyStoreAsync();
        await using var library = await Library.CreateAsync();
        await library.SeedAsync(tags: ["Anime", "Sample"], performers: []);

        await InitializeAsync(store, library.BuildProvider());
        Assert.Equal(OptionsMigration.CurrentSchema, await store.GetAsync(OptionsMigration.SchemaKey));
        string afterFirst = (await new OptionsStore(store).LoadRawAsync())!;
        int setsAfterFirst = store.SetCallCount;

        // A second load is a host restart, redeploy or reboot. Two independent things stop it from
        // repeating — the stamp and the already-converted shape — so this asserts the outcome; the
        // stamp's own contribution is pinned separately below.
        await InitializeAsync(store, library.BuildProvider());

        Assert.Equal(setsAfterFirst, store.SetCallCount);
        Assert.Equal(afterFirst, await new OptionsStore(store).LoadRawAsync());
    }

    [Fact]
    public async Task OnceStamped_TheStoredBlobIsNotEvenRead()
    {
        // What the stamp actually buys, and the only test that depends on it. Idempotence does NOT:
        // a converted blob is already id-keyed, so the shape check alone refuses a second pass —
        // measured by deleting the stamp short-circuit and watching every other test stay green.
        var store = await NewLegacyStoreAsync();
        await store.SetAsync(OptionsMigration.SchemaKey, OptionsMigration.CurrentSchema);
        store.GetKeys.Clear();

        // No DbContext registered: a stamp that failed to short-circuit would reach for one and throw.
        var services = new ServiceCollection();
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        await InitializeAsync(store, services.BuildServiceProvider());

        Assert.DoesNotContain(ExtensionOptionsStore<RenamerOptions>.Key, store.GetKeys);
    }

    [Fact]
    public async Task AStoreThatIsAlreadyIdKeyed_IsNotTouched_AndNeedsNoDatabase()
    {
        // A fresh install already stores ids. It must not pay for a database read, and — the dangerous
        // half — its int-spelled TagDestinations keys must never be re-read as tag NAMES.
        var store = await NewStoreAsync();
        await new OptionsStore(store).SaveAsync(
            new RenamerOptions { TagDestinations = { [9] = "/media/anime" }, ExcludeTagIds = { 15 } });
        int setsBefore = store.SetCallCount;

        // No DbContext registered: reaching for one at all would throw and be logged as a failure.
        var services = new ServiceCollection();
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        await InitializeAsync(store, services.BuildServiceProvider());

        Assert.Equal(setsBefore, store.SetCallCount);
        var options = await LoadOptionsAsync(store);
        Assert.Equal(new Dictionary<int, string> { [9] = "/media/anime" }, options.TagDestinations);
        Assert.Equal([15], options.ExcludeTagIds);
    }

    // ── The dropped-name path, end to end ─────────────────────────────────────

    [Fact]
    public async Task ANameMatchingNoRow_IsDroppedWhileTheRestOfTheBlobConverts()
    {
        var store = await NewStoreAsync();
        await new OptionsStore(store).SaveRawAsync(
            """{"ExcludeTags":["Sample","tag-that-no-longer-exists"]}""");

        await using var library = await Library.CreateAsync();
        await library.SeedAsync(tags: ["Sample"], performers: []);

        await InitializeAsync(store, library.BuildProvider());

        Assert.Single((await LoadOptionsAsync(store)).ExcludeTagIds);
        Assert.Equal(OptionsMigration.CurrentSchema, await store.GetAsync(OptionsMigration.SchemaKey));
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static async Task<FakeStore> NewStoreAsync()
    {
        var store = new FakeStore();
        await store.SetAsync(RevertLog.SchemaKey, RevertLog.CurrentSchema);
        return store;
    }

    private static async Task<FakeStore> NewLegacyStoreAsync()
    {
        var store = await NewStoreAsync();
        await new OptionsStore(store).SaveRawAsync(LegacyBlob);
        return store;
    }

    private static async Task AssertStillLegacyAsync(FakeStore store)
    {
        string? stored = await new OptionsStore(store).LoadRawAsync();
        Assert.Equal(LegacyBlob, stored);
        Assert.True(OptionsMigration.Scan(stored).Tags > 0, "the blob is no longer readable in its original form");
        Assert.Null(await store.GetAsync(OptionsMigration.SchemaKey));
    }

    private static Task<RenamerOptions> LoadOptionsAsync(FakeStore store) =>
        new OptionsStore(store).LoadAsync();

    private static async Task InitializeAsync(FakeStore store, IServiceProvider provider)
    {
        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);
    }

    /// <summary>
    /// A throwaway SQLite-backed Cove database, the principal its contexts read, and a record of which
    /// principal was in effect for each SQL command they ran.
    /// </summary>
    private sealed class Library : IAsyncDisposable
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

        public ServiceProvider BuildProvider()
        {
            var services = new ServiceCollection();
            services.AddSingleton<ICurrentPrincipalAccessor>(Principals);
            services.AddScoped<DbContext>(_ => NewContext());
            services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
            return services.BuildServiceProvider();
        }

        public ValueTask DisposeAsync() => _conn.DisposeAsync();
    }

    /// <summary>
    /// Records the ambient principal at the moment a command actually executes — the instant Cove's
    /// authorization filters consult it, and therefore the only point at which "this read ran elevated"
    /// is a fact rather than an inference about where a call happens to sit in the source.
    /// </summary>
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
