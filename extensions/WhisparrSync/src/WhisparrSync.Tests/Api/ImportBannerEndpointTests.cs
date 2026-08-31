using System.Text.Json;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Import;
using WhisparrSync.Options;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The banner read: its gate, the projection it answers with, and the spelling that projection
/// serializes in.
/// </summary>
/// <remarks>
/// The deny path is paired with a caller who does hold the gate. Without that control a 403 could
/// equally mean the handler is broken for everyone.
/// </remarks>
public sealed class ImportBannerEndpointTests
{
    /// <summary>The settings the host serializes an extension's responses with.</summary>
    private static readonly JsonSerializerOptions HostJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Every spelling <see cref="ImportRefusalCause"/> reaches the wire in, transcribed by hand from
    /// the enum's own member names.
    /// </summary>
    /// <remarks>
    /// Written out rather than computed from the enum. An expectation derived from the type it checks
    /// agrees with it whatever the converter does, so it would report nothing on the day the
    /// declaration moves off the type and an options-level converter outranks it.
    /// </remarks>
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

    /// <summary>
    /// An aggregate with no entries answers with an empty list, which the surface renders nothing
    /// for. A null would be a second empty the surface would have to know about.
    /// </summary>
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

    /// <summary>
    /// The count reaches the surface as the integer that was stored.
    /// </summary>
    /// <remarks>
    /// A value larger than the paths listed beside it, so a projection deriving the count from the
    /// list rather than reading it fails here.
    /// </remarks>
    [Fact]
    public async Task TheCountIsTheStoredIntegerRatherThanTheNumberOfPathsListed()
    {
        var (_, options) = await StoredAsync(RootWith("/whisparr-media", 412, 3));

        var view = ViewIn(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            Configure(), options, TestCt));

        Assert.Equal(412, Assert.Single(view.Roots).CountSinceLastSuccess);
        Assert.Equal(3, view.Roots[0].NewestPaths.Count);
    }

    /// <summary>
    /// The line counted under no reporting root survives the projection, keeping its blank key.
    /// </summary>
    /// <remarks>
    /// That key is what a delivery falling under none of the instance's own roots is counted under,
    /// so dropping it here would lose exactly the misconfiguration this surface exists for. Naming it
    /// is the surface's job, not this projection's.
    /// </remarks>
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

    /// <summary>
    /// Every cause serializes in the camelCase spelling, against literals written out by hand.
    /// </summary>
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

    /// <summary>
    /// The response's property names are camelCase, pinned against the whole serialized body.
    /// </summary>
    /// <remarks>
    /// The stored blob these values come from is PascalCase, so a projection that handed the stored
    /// type straight to the serializer would still answer with the values a reader wants and in the
    /// wrong spelling.
    /// </remarks>
    [Fact]
    public async Task TheResponseIsAllCamelCase()
    {
        var (_, options) = await StoredAsync(new ImportRootRefusals
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
        });

        var body = Serialize(ViewIn(await global::WhisparrSync.WhisparrSync.ReadImportBannerAsync(
            Configure(), options, TestCt)));

        Assert.Equal(
            "{\"roots\":[{\"root\":\"/whisparr-media\",\"countSinceLastSuccess\":2,"
                + "\"newestPaths\":[{\"path\":\"/whisparr-media/a.mp4\","
                + "\"cause\":\"notFoundUnderAnyRoot\"}]}]}",
            body);
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static FakePrincipalAccessor Configure()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    private static string Serialize(ImportBannerView view)
        => JsonSerializer.Serialize(view, HostJsonOptions);

    private static ImportBannerView ViewIn(IResult result)
        => Assert.IsType<ImportBannerView>(
            Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    /// <summary>One root's line, holding <paramref name="paths"/> paths that all differ.</summary>
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

    /// <summary>
    /// A store holding <paramref name="refusals"/>, written through the options store the handler
    /// reads back through.
    /// </summary>
    private static async Task<(FakeStore Store, OptionsStore Options)> StoredAsync(
        params ImportRootRefusals[] refusals)
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        await options.SaveAsync(
            new WhisparrSyncOptions { ImportRefusals = [.. refusals] },
            TestContext.Current.CancellationToken);
        return (store, options);
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;
}
