using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http.HttpResults;
using Renamer.Contracts;
using Renamer.Options;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

/// <summary>
/// The <c>/library-paths</c> endpoint, and the one property that makes it usable: the spelling it
/// emits is the spelling a destination root is STORED in.
/// </summary>
/// <remarks>
/// The settings panel has no other source for a root — it copies a string out of this response and
/// later asks whether a stored root is still in the list, by exact equality. So the two spellings have
/// to be one spelling, and they were not: Cove hands its paths back in the platform's own form
/// (<c>G:\Downloads\P</c> on Windows) while the one-time conversion writes the root
/// <c>PathConfinement.ContainingRoot</c> returns, which is normalized. Every converted rule then
/// rendered as pointing at a path Cove no longer had, while the planner — which normalizes on both
/// sides of every comparison it makes — went on applying those same rules correctly. A panel telling a
/// user their working rules are skipped is worse than one that breaks, because the repair it invites
/// is the damage.
/// <para>
/// Why this tier and not the pure one below it: the endpoint's answer is what the panel actually
/// receives, and an L0 test of the projection alone would stay green against a handler that emitted
/// something else. The initialize seam is likewise the real one — the conversion runs against the
/// same host configuration the endpoint reads, which is the whole point.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
public sealed class LibraryPathsEndpointTests
{
    /// <summary>
    /// Cove's own spelling of a library path, transcribed from a running host rather than composed
    /// here — this is the pair the owner's Cove reported, backslashes and all.
    /// </summary>
    private static readonly string[] HostLibraryPaths = [@"G:\Downloads\P", @"I:\Downloads\P"];

    /// <summary>A pre-conversion store: one studio rule whose typed root sits under a library path.</summary>
    private const string StoredRuleUnderALibraryPath =
        """{"StudioDestinations":{"101":"I:\\Downloads\\P\\videos"}}""";

    [Fact]
    public async Task TheSpellingItEmits_IsTheSpellingADestinationRootIsStoredIn()
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveRawAsync(StoredRuleUnderALibraryPath);

        await using var library = await Library.CreateAsync();
        var extension = RenamerFixture.Create();
        ((IStatefulExtension)extension).SetStore(store);
        await extension.InitializeAsync(library.BuildProvider(null, HostLibraryPaths));

        // Side one: the STORE, written by the real one-time conversion during initialize.
        var stored = await new OptionsStore(store).LoadAsync();
        string storedRoot = stored.StudioDestinations[101].Root;

        // Side two: the ENDPOINT, answering the request the settings page makes on mount.
        var response = extension.LibraryPaths(FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));
        var emitted = Assert.IsType<Ok<LibraryPathsView>>(Unwrap(response)).Value!.Paths;

        // Neither expectation is written here. Both sides were computed by production code from the one
        // raw host spelling above, and the assertion is that two independent producers agree — which is
        // exactly the test the panel performs (`Array.includes`, an ordinal string compare). A test that
        // named the canonical spelling itself would agree with whichever side it copied it from.
        Assert.Contains(storedRoot, emitted, StringComparer.Ordinal);

        // And the conversion really ran: a deferred or skipped conversion would leave the root empty,
        // which is the sentinel meaning "the file's own library path" and is in no list at all — so the
        // line above would fail rather than pass vacuously, but it would fail for the wrong reason.
        Assert.NotEqual(string.Empty, storedRoot);
    }
}
