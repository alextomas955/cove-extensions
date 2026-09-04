using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cove.Core.Auth;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Tests.Jobs;

/// <summary>
/// The one line a reflect-owned run that started by itself is reported on, for each of the reasons
/// it can reach no folder for.
/// </summary>
/// <remarks>
/// The run's line is the ONLY place this path is reported. The gesture that started it was answered
/// before the instance's setting was read again, so a run that says "0 linked, 0 refused." about a
/// setting that stopped it tells the reader nothing they can act on.
/// <para>
/// Every expected sentence here is transcribed by hand from the source. Composing one from
/// <c>SentenceFor</c> would agree with a sentence that changed underneath it.
/// </para>
/// </remarks>
public sealed class ReflectOwnedJobTests
{
    /// <summary>One folder's parse answer, with everything an attach has to be composed from.</summary>
    private const string Attachable = """
        [{"path":"/library/one/scene.mp4","folderName":"one",
          "quality":{"quality":{"id":7}},"languages":[{"id":1}],"movie":{"id":31}}]
        """;

    private const string SettingIsOff = "No files were linked: Whisparr's hard-link setting is off.";

    private const string SettingUnreadable =
        "No files were linked: Whisparr's hard-link setting could not be read.";

    private const string AttachedNothing = "0 linked, 0 refused.";

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

    /// <summary>
    /// No connection configured, or a connected generation holding no reflect-owned role, is not a
    /// fact about the instance's setting. Naming one would send the reader to a value nobody read.
    /// </summary>
    [Fact]
    public async Task ARunThatCouldNotBeAimedForAnyOtherReasonReportsAsARunThatAttachedNothing()
    {
        var run = await RunAsync(OneStudio, (_, _) => Task.FromResult(new ReflectOwnedAim(null, null)));

        var line = ReflectOwnedJob.SummaryOf(run);
        Assert.Equal(AttachedNothing, line);
        Assert.DoesNotContain("setting", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A run nobody can read stays a clean no-op, and is never aimed at anything.</summary>
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

    /// <summary>
    /// A stopped run keeps the ending that tells the reader it did not finish. What it linked before
    /// the stop is on the instance and there is nothing to undo.
    /// </summary>
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

    /// <summary>An aim that reached nothing because <paramref name="reason"/> stopped it.</summary>
    private static Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAim>> Stopped(
        ReflectOwnedSkipReason reason)
        => (_, _) => Task.FromResult(new ReflectOwnedAim(null, reason));

    /// <summary>An aim that acts, reading and attaching through the delegates supplied.</summary>
    private static Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAim>> Acting(
        Func<string, CancellationToken, Task<ImportableListing>> read,
        Func<JsonArray, CancellationToken, Task<bool>> attach)
        => (_, _) => Task.FromResult(
            new ReflectOwnedAim(new ReflectOwnedAiming(WhisparrGeneration.V3, read, attach), null));

    private static Task<ReflectOwnedRun> RunAsync(
        ReflectOwnedBatch batch,
        Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAim>> aiming,
        params string[] folders)
        => RunAsync(batch, aiming, TestContext.Current.CancellationToken, folders);

    private static async Task<ReflectOwnedRun> RunAsync(
        ReflectOwnedBatch batch,
        Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAim>> aiming,
        CancellationToken ct,
        params string[] folders)
    {
        var services = new ServiceCollection();
        services.AddScoped<ICurrentPrincipalAccessor>(_ => FakePrincipalAccessor.WithPermissions());
        services.AddScoped<IEntityFolderPort>(_ => new FixedFolders(folders));
        await using var provider = services.BuildServiceProvider();

        return await ReflectOwnedJob.RunAsync(
            batch, provider.GetRequiredService<IServiceScopeFactory>(), aiming, ct);
    }

    /// <summary>The folders one entity holds files in, as this case supplies them.</summary>
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
    }
}
