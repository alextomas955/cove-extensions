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
/// Renamer has eight <c>RunAsSystemAsync</c> sites and, before this, exactly one of them was
/// observable by any test — <c>Library</c>/<c>PrincipalRecorder</c> lived as private classes in the
/// options-migration file, so nothing else could see the principal at all. That mattered most here:
/// the host dispatches entity events fire-and-forget, so this flow carries whichever principal made
/// the edit, or none, and under an unelevated principal Cove's authorization filters return zero rows
/// SUCCESSFULLY. The hook would then silently rename nothing, which is indistinguishable from an empty
/// library.
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

        // Anonymous is what a fire-and-forget dispatch carries when no principal rode along with it.
        library.Principals.Set(CovePrincipal.Anonymous());
        library.PrincipalPerCommand.Clear();

        await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

        // Non-empty first: an all-System verdict over zero commands would be a vacuous pass, and a hook
        // that never reached the database at all is exactly the failure this is here to catch.
        Assert.NotEmpty(library.PrincipalPerCommand);
        Assert.All(library.PrincipalPerCommand, kind => Assert.Equal(PrincipalKind.System, kind));

        // Elevation is a span, not a mode: the caller's principal is put back afterwards.
        Assert.Equal(PrincipalKind.Anonymous, library.Principals.Current!.Kind);
    }
}
