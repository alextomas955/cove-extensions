using Cove.Core.Auth;
using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// The auto-renamer hook's background database work runs as System.
/// </summary>
/// <remarks>
/// The host dispatches entity events fire-and-forget, so this flow carries whichever principal made the
/// edit. Under a present but under-privileged one — the dangerous case, and what this test drives —
/// Cove's authorization filters return zero rows SUCCESSFULLY: the hook then silently renames nothing,
/// which is indistinguishable from an empty library. A dispatch carrying NO principal is the safe case
/// rather than the dangerous one, because <c>CoveContext</c> bypasses those filters for a null principal
/// exactly as it does for System — which is why an absent principal must never stand in for an
/// unprivileged one here.
/// <para>
/// Asserted on the principal AT THE COMMAND rather than on a row count, for the reason
/// <c>Library</c> documents: <c>CoveContext</c> installs those filters only under Npgsql, so this tier
/// cannot reproduce the zero-row consequence — and the e2e tier runs with auth off, so it cannot
/// either. The principal in effect when the reader executes IS the fact the filters consult, and it
/// stays true whichever provider is underneath.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
public sealed class AutoRenamerElevationTests
{
    [Fact]
    public async Task TheHooksReads_ExecuteUnderSystem_AndLeaveTheCallersPrincipalBehindThem()
    {
        using var dir = new TempDir();
        await using var library = await Library.CreateAsync();

        string folderPath = dir.Root.Replace('\\', '/');
        int videoId;
        await using (var seed = library.NewContext())
        {
            (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(seed, folderPath, "raw.mkv", "My Film");
        }

        File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(
            new RenamerOptions { AutoRenamerOnUpdate = true, FilenameTemplate = "$title" });

        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(library.BuildProvider());

        // Present but unprivileged, which is the case the elevation exists for. Leaving the accessor
        // empty instead would prove the safe case: no principal bypasses the filters anyway.
        library.Principals.Set(CovePrincipal.Anonymous());
        library.CommandsExecuted.Clear();

        await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

        // Non-empty first: an all-System verdict over zero commands would be a vacuous pass, and a hook
        // that never reached the database at all is exactly the failure this is here to catch.
        Assert.NotEmpty(library.CommandsExecuted);
        Assert.All(library.CommandsExecuted, c => Assert.Equal(PrincipalKind.System, c.Principal));

        // Elevation is a span, not a mode: the caller's principal is put back afterwards.
        Assert.Equal(PrincipalKind.Anonymous, library.Principals.Current!.Kind);
    }
}
