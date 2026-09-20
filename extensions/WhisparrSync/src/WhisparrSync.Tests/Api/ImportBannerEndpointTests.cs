using System.Text.Json;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Import;
using WhisparrSync.Options;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Api;

// The banner read sits at the configure tier. The deny case is paired with a caller who holds the
// tier, because a 403 alone could mean the handler is broken for everyone.
public sealed class ImportBannerEndpointTests
{
    // The settings the host serializes an extension's responses with.
    private static readonly JsonSerializerOptions HostJsonOptions = new(JsonSerializerDefaults.Web);

    // Transcribed by hand. An expectation computed from the enum agrees with it whatever the
    // converter does, so it would say nothing on the day an options-level converter outranks the
    // declaration on the type.
    private static readonly (ImportRefusalCause Cause, string Wire)[] CauseSpellings =
    [
        (ImportRefusalCause.NotFoundUnderAnyRoot, "notFoundUnderAnyRoot"),
        (ImportRefusalCause.AmbiguousCandidates, "ambiguousCandidates"),
        (ImportRefusalCause.Unreadable, "unreadable"),
    ];

    [Fact]
    public async Task TheBannerReadRefusesACallerWithoutTheConfigureTierAndReadsNothing()
    {
        var (store, options) = await StoredAsync(
            new ImportRootRefusals { Root = "/whisparr-media", CountSinceLastSuccess = 1 });
        store.GetKeys.Clear();

        var refused = await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), options, TestCt);

        Assert.Equal(403, StatusOf(refused));
        Assert.Empty(store.GetKeys);

        var answered = await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            Configure(), options, TestCt);

        Assert.NotEqual(403, StatusOf(answered));
        Assert.NotEmpty(store.GetKeys);
    }

    [Fact]
    public async Task ACallerWithNoPrincipalAtAllIsRefused()
    {
        var (_, options) = await StoredAsync();

        Assert.Equal(
            403,
            StatusOf(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
                FakePrincipalAccessor.NullPrincipal(), options, TestCt)));
    }

    // An empty list, not null: a null would be a second empty the surface has to know about.
    [Fact]
    public async Task AnAggregateWithNoEntriesProjectsAnEmptyList()
    {
        var (_, options) = await StoredAsync();

        var view = ViewIn(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            Configure(), options, TestCt));

        Assert.NotNull(view.Roots);
        Assert.Empty(view.Roots);
        Assert.Contains("\"roots\":[]", Serialize(view), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreeRootsProjectThreeLinesEachHoldingAtMostThreePaths()
    {
        var (_, options) = await StoredAsync(
            RootWith("/whisparr-media", 7, 3),
            RootWith("/whisparr-elsewhere", 2, 2),
            RootWith("/whisparr-third", 1, 1));

        var view = ViewIn(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            Configure(), options, TestCt));

        Assert.Equal(
            ["/whisparr-media", "/whisparr-elsewhere", "/whisparr-third"],
            view.Roots.Select(line => line.Root));
        Assert.Equal([7, 2, 1], view.Roots.Select(line => line.CountSinceLastSuccess));
        Assert.Equal([3, 2, 1], view.Roots.Select(line => line.NewestPaths.Count));
        Assert.All(
            view.Roots,
            line => Assert.True(line.NewestPaths.Count <= ImportRootRefusals.NewestPathsKept));
    }

    // The stored count is larger than the paths listed beside it, so a projection deriving the
    // count from the list fails here.
    [Fact]
    public async Task TheCountIsTheStoredIntegerRatherThanTheNumberOfPathsListed()
    {
        var (_, options) = await StoredAsync(RootWith("/whisparr-media", 412, 3));

        var view = ViewIn(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            Configure(), options, TestCt));

        Assert.Equal(412, Assert.Single(view.Roots).CountSinceLastSuccess);
        Assert.Equal(3, view.Roots[0].NewestPaths.Count);
    }

    // The blank key counts a delivery falling under none of the instance's roots. Dropping it
    // would lose the misconfiguration this surface exists for.
    [Fact]
    public async Task TheLineCountedUnderNoReportingRootIsProjectedRatherThanDropped()
    {
        var (_, options) = await StoredAsync(
            RootWith(ImportRefusalProjector.NoReportedRoot, 4, 1),
            RootWith("/whisparr-media", 1, 1));

        var view = ViewIn(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            Configure(), options, TestCt));

        Assert.Equal(["", "/whisparr-media"], view.Roots.Select(line => line.Root));
        Assert.Equal(4, view.Roots[0].CountSinceLastSuccess);
    }

    [Fact]
    public async Task ThePathAndItsCauseTravelTogether()
    {
        var (_, options) = await StoredAsync(new ImportRootRefusals
        {
            Root = "/whisparr-media",
            CountSinceLastSuccess = 3,
            NewestPaths =
            [
                new ImportRefusalEntry
                {
                    Path = "/whisparr-media/c.mp4",
                    Cause = ImportRefusalCause.Unreadable,
                },
                new ImportRefusalEntry
                {
                    Path = "/whisparr-media/b.mp4",
                    Cause = ImportRefusalCause.AmbiguousCandidates,
                },
                new ImportRefusalEntry
                {
                    Path = "/whisparr-media/a.mp4",
                    Cause = ImportRefusalCause.NotFoundUnderAnyRoot,
                },
            ],
        });

        var view = ViewIn(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            Configure(), options, TestCt));

        Assert.Equal(
            [
                ("/whisparr-media/c.mp4", ImportRefusalCause.Unreadable),
                ("/whisparr-media/b.mp4", ImportRefusalCause.AmbiguousCandidates),
                ("/whisparr-media/a.mp4", ImportRefusalCause.NotFoundUnderAnyRoot),
            ],
            Assert.Single(view.Roots).NewestPaths.Select(path => (path.Path, path.Cause)));
    }

    [Fact]
    public async Task EveryCauseSerializesInTheSpellingTheWireDocumentDeclares()
    {
        foreach (var (cause, wire) in CauseSpellings)
        {
            var (_, options) = await StoredAsync(new ImportRootRefusals
            {
                Root = "/whisparr-media",
                CountSinceLastSuccess = 1,
                NewestPaths = [new ImportRefusalEntry { Path = "/whisparr-media/a.mp4", Cause = cause }],
            });

            var body = Serialize(ViewIn(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
                Configure(), options, TestCt)));

            Assert.Contains($"\"cause\":\"{wire}\"", body, StringComparison.Ordinal);
        }
    }

    // The stored total is larger than every refusal beside it, so a projection deriving the figure
    // from the rows it can see fails here.
    [Fact]
    public async Task TheContainmentReachesTheSurfaceAsTheStoredScalars()
    {
        var (_, options) = await StoredAsync(new WhisparrSyncOptions
        {
            ImportRefusals = [RootWith("/whisparr-media", 2, 1)],
            ImportHealth = new ImportHealthAggregate
            {
                RecordsContained = 41,
                LastContainedAtUtc = Contained,
            },
        });

        var view = ViewIn(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            Configure(), options, TestCt));

        Assert.Equal(41, view.RecordsContained);
        Assert.Equal(Contained, view.LastContainedAtUtc);
    }

    // The two halves are stored by different writers, so a projection keyed on the refusals having
    // entries would answer an empty page here.
    [Fact]
    public async Task ContainmentIsProjectedWithNoRefusalsRecorded()
    {
        var (_, options) = await StoredAsync(new WhisparrSyncOptions
        {
            ImportHealth = new ImportHealthAggregate
            {
                RecordsContained = 3,
                LastContainedAtUtc = Contained,
            },
        });

        var view = ViewIn(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            Configure(), options, TestCt));

        Assert.Empty(view.Roots);
        Assert.Equal(3, view.RecordsContained);
    }

    // The stored blob these values come from is PascalCase, so a projection handing the stored type
    // straight to the serializer answers the right values in the wrong spelling.
    [Fact]
    public async Task TheResponseIsAllCamelCase()
    {
        var (_, options) = await StoredAsync(new WhisparrSyncOptions
        {
            ImportRefusals =
            [
                new ImportRootRefusals
                {
                    Root = "/whisparr-media",
                    CountSinceLastSuccess = 2,
                    NewestPaths =
                    [
                        new ImportRefusalEntry
                        {
                            Path = "/whisparr-media/a.mp4",
                            Cause = ImportRefusalCause.NotFoundUnderAnyRoot,
                        },
                    ],
                },
            ],
            ImportHealth = new ImportHealthAggregate
            {
                RecordsContained = 41,
                LastContainedAtUtc = Contained,
            },
        });

        var body = Serialize(ViewIn(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            Configure(), options, TestCt)));

        Assert.Equal(
            "{\"roots\":[{\"root\":\"/whisparr-media\",\"countSinceLastSuccess\":2,"
                + "\"newestPaths\":[{\"path\":\"/whisparr-media/a.mp4\","
                + "\"cause\":\"notFoundUnderAnyRoot\"}]}],"
                + "\"recordsContained\":41,\"lastContainedAtUtc\":\"2026-08-31T09:00:00+00:00\"}",
            body);
    }

    private static readonly DateTimeOffset Contained = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static FakePrincipalAccessor Configure()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    private static string Serialize(ImportBannerView view)
        => JsonSerializer.Serialize(view, HostJsonOptions);

    private static ImportBannerView ViewIn(IResult result)
        => Assert.IsType<ImportBannerView>(
            Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    private static ImportRootRefusals RootWith(string root, int count, int paths)
        => new()
        {
            Root = root,
            CountSinceLastSuccess = count,
            NewestPaths =
            [
                .. Enumerable.Range(0, paths).Select(index => new ImportRefusalEntry
                {
                    Path = $"{root}/{index}.mp4",
                    Cause = ImportRefusalCause.NotFoundUnderAnyRoot,
                }),
            ],
        };

    private static Task<(FakeStore Store, OptionsStore Options)> StoredAsync(
        params ImportRootRefusals[] refusals)
        => StoredAsync(new WhisparrSyncOptions { ImportRefusals = [.. refusals] });

    private static async Task<(FakeStore Store, OptionsStore Options)> StoredAsync(
        WhisparrSyncOptions stored)
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        await options.SaveAsync(stored, TestContext.Current.CancellationToken);
        return (store, options);
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;
}
