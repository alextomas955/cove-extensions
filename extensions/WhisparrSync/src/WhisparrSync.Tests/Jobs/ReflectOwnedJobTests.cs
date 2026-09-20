using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cove.Core.Auth;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Tests.Jobs;

// The run's line is the only place this path is reported, so it has to carry the reason. Every
// expected sentence is transcribed by hand from the source. Composing one from SentenceFor would
// agree with a sentence that changed underneath it.
public sealed class ReflectOwnedJobTests
{
    private const string Attachable = """
        [{"path":"/library/one/scene.mp4","folderName":"one",
          "quality":{"quality":{"id":7}},"languages":[{"id":1}],"movie":{"id":31}}]
        """;

    private const string SettingIsOff = "No files were linked: Whisparr's hard-link setting is off.";

    private const string SettingUnreadable =
        "No files were linked: Whisparr's hard-link setting could not be read.";

    private const string AttachedNothing = "0 linked, 0 refused.";

    private const string NoRootToCompare =
        "No files were linked: Whisparr declared no root folder, so whether a link would copy the "
        + "data could not be checked.";

    [Fact]
    public async Task ARunTheLinkingSettingStoppedSaysWhichSettingStoppedIt()
    {
        var run = await RunAsync(OneStudio, Stopped(ReflectOwnedSkipReason.HardLinksOff));

        var line = ReflectOwnedJob.SummaryOf(run);
        Assert.Equal(SettingIsOff, line);

        // A line carrying both would say two things about one run, and the counts of a run that
        // reached no folder are zero for a reason that has nothing to do with what it linked.
        Assert.DoesNotContain("linked,", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunWhoseLinkingSettingCouldNotBeReadSaysThatInstead()
    {
        var run = await RunAsync(
            OneStudio, Stopped(ReflectOwnedSkipReason.HardLinkSettingUnreadable));

        var line = ReflectOwnedJob.SummaryOf(run);
        Assert.Equal(SettingUnreadable, line);
        Assert.NotEqual(SettingIsOff, line);
    }

    // The root list is what the cross-root guard compares against, and an import that crosses two
    // roots copies the bytes in full. A run that linked with the guard unapplied would read the
    // same as one that linked safely, so it stops instead and says why.
    [Fact]
    public async Task ARunWhoseInstanceRootsCouldNotBeReadLinksNothingAndSaysSo()
    {
        var attaches = 0;

        var run = await RunAsync(
            OneStudio,
            Acting(
                (_, _) => Task.FromResult(ImportableListing.Listed(Attachable)),
                (_, _) =>
                {
                    attaches++;
                    return Task.FromResult(true);
                }),
            new UnreadableRoots(),
            TestContext.Current.CancellationToken,
            "/library/one");

        Assert.Equal(NoRootToCompare, ReflectOwnedJob.SummaryOf(run));
        Assert.Equal(0, attaches);
    }

    // An aim that failed for any other reason says nothing about the hard-link setting. Naming the
    // setting would send the reader to a value nobody read.
    [Fact]
    public async Task ARunThatCouldNotBeAimedForAnyOtherReasonReportsAsARunThatAttachedNothing()
    {
        var run = await RunAsync(OneStudio, (_, _) => Task.FromResult(new ReflectOwnedAim(null, null)));

        var line = ReflectOwnedJob.SummaryOf(run);
        Assert.Equal(AttachedNothing, line);
        Assert.DoesNotContain("setting", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARunNamingNoEntityReportsAsARunThatAttachedNothing()
    {
        var aimed = 0;

        var run = await RunAsync(
            ReflectOwnedJob.Decode(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["coveId"] = "7",
            }),
            (_, _) =>
            {
                aimed++;
                return Task.FromResult(new ReflectOwnedAim(null, ReflectOwnedSkipReason.HardLinksOff));
            });

        Assert.Equal(AttachedNothing, ReflectOwnedJob.SummaryOf(run));
        Assert.Equal(0, aimed);
    }

    [Fact]
    public async Task ARunThatLinkedSomethingStillReportsItsCounts()
    {
        var run = await RunAsync(
            OneStudio,
            Acting(
                (_, _) => Task.FromResult(ImportableListing.Listed(Attachable)),
                (_, _) => Task.FromResult(true)),
            "/library/one");

        var line = ReflectOwnedJob.SummaryOf(run);
        Assert.Equal("1 linked, 0 refused.", line);
        Assert.DoesNotContain("setting", line, StringComparison.OrdinalIgnoreCase);
    }

    // Files linked before the stop are on the instance and there is nothing to undo.
    [Fact]
    public async Task ACancelledRunKeepsItsEnding()
    {
        using var stopping = new CancellationTokenSource();

        var run = await RunAsync(
            OneStudio,
            Acting(
                (_, _) => Task.FromResult(ImportableListing.Listed(Attachable)),
                (_, _) =>
                {
                    stopping.Cancel();
                    return Task.FromResult(true);
                }),
            stopping.Token,
            "/library/one",
            "/library/two");

        Assert.Null(run.Skipped);
        Assert.Equal("1 linked, 0 refused, then stopped.", ReflectOwnedJob.SummaryOf(run));
    }

    private static ReflectOwnedBatch OneStudio => new(WhisparrEntityKind.Studio, 7);

    private static Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAim>> Stopped(
        ReflectOwnedSkipReason reason)
        => (_, _) => Task.FromResult(new ReflectOwnedAim(null, reason));

    private static Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAim>> Acting(
        Func<string, CancellationToken, Task<ImportableListing>> read,
        Func<JsonArray, CancellationToken, Task<bool>> attach)
        => (_, _) => Task.FromResult(
            new ReflectOwnedAim(
                new ReflectOwnedAiming(
                    WhisparrGeneration.V3,
                    (folder, _) => Task.FromResult(
                        new AddressedFolder(folder, null, "/config/library", [])),
                    read,
                    attach),
                null));

    private static Task<ReflectOwnedRun> RunAsync(
        ReflectOwnedBatch batch,
        Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAim>> aiming,
        params string[] folders)
        => RunAsync(batch, aiming, TestContext.Current.CancellationToken, folders);

    private static Task<ReflectOwnedRun> RunAsync(
        ReflectOwnedBatch batch,
        Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAim>> aiming,
        CancellationToken ct,
        params string[] folders)
        => RunAsync(batch, aiming, new NoDeclaredRoots(), ct, folders);

    private static async Task<ReflectOwnedRun> RunAsync(
        ReflectOwnedBatch batch,
        Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAim>> aiming,
        IReportedRootPort roots,
        CancellationToken ct,
        params string[] folders)
    {
        var services = new ServiceCollection();
        services.AddScoped<ICurrentPrincipalAccessor>(_ => FakePrincipalAccessor.WithPermissions());
        services.AddScoped<IEntityFolderPort>(_ => new FixedFolders(folders));
        services.AddScoped(_ => roots);
        await using var provider = services.BuildServiceProvider();

        return await ReflectOwnedJob.RunAsync(
            batch, provider.GetRequiredService<IServiceScopeFactory>(), aiming, ct);
    }

    private sealed class FixedFolders(string[] folders) : IEntityFolderPort
    {
        public async IAsyncEnumerable<string> FoldersFor(
            WhisparrEntityKind kind,
            int coveId,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                yield return folder;
            }

            await Task.CompletedTask;
        }

        public Task<int> FilesUnderAsync(
            WhisparrEntityKind kind, int coveId, string coveRoot, CancellationToken ct)
            => throw new NotSupportedException("This case is about the folder loop.");

        public Task<int> VideoFilesUnderAsync(int videoId, string coveRoot, CancellationToken ct)
            => throw new NotSupportedException("This case is about the folder loop.");
    }

    private sealed class NoDeclaredRoots : IReportedRootPort
    {
        public Task<IReadOnlyList<string>?> ReadAsync(
            WhisparrGeneration generation, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>?>([]);
    }

    private sealed class UnreadableRoots : IReportedRootPort
    {
        public Task<IReadOnlyList<string>?> ReadAsync(
            WhisparrGeneration generation, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>?>(null);
    }
}
