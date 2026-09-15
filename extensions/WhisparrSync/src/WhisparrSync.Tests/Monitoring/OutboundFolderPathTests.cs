using Cove.Core.Interfaces;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// The folder this product hands an instance, driven through the whole chain in process.
/// </summary>
/// <remarks>
/// A Cove root spelled for one operating system reaches a container that has no such drive. Handing
/// the library's own spelling over produces a legitimately empty listing and a run reporting a clean
/// zero, which is a failure nothing in the product could see.
/// <para>
/// Asserted on the folder the importable request actually carried, rather than on what a seam was
/// handed: the query is composed below the level a call site can see.
/// </para>
/// </remarks>
public sealed class OutboundFolderPathTests
{
    /// <summary>The library root as Cove has it, on a machine the instance does not share.</summary>
    private const string CoveRoot = "G:/Downloads/P";

    /// <summary>The same content as the container sees it.</summary>
    private const string InstanceRoot = "/data";

    private const string Folder = CoveRoot + "/Blue Harbor";

    private const long SampleSize = 41;

    private const string LinksIntoPlace = """{"copyUsingHardlinks":true}""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// The folder the importable request carries is the instance's own path, not the library's.
    /// </summary>
    [Fact]
    public async Task TheImportableRequestCarriesTheInstancesOwnPath()
    {
        var (host, _) = await RunAsync();
        await using var driven = host;

        var importable = Assert.Single(
            host.Bytes!.Targets,
            sent => sent.Contains("manualimport", StringComparison.Ordinal));

        Assert.Contains("folder=%2fdata%2fBlue+Harbor", importable, StringComparison.Ordinal);
        Assert.DoesNotContain("Downloads", importable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The instance is asked what it holds at the candidate before it is handed a folder.</summary>
    [Fact]
    public async Task TheInstanceIsAskedAboutTheCandidateBeforeItIsHandedAFolder()
    {
        var (host, _) = await RunAsync();
        await using var driven = host;

        var probe = host.Bytes!.Targets.FindIndex(
            sent => sent.Contains("filesystem", StringComparison.Ordinal));
        var listing = host.Bytes.Targets.FindIndex(
            sent => sent.Contains("manualimport", StringComparison.Ordinal));

        Assert.True(probe >= 0);
        Assert.True(listing > probe);
    }

    /// <summary>
    /// A folder whose root agrees on nothing reaches the listing not at all.
    /// </summary>
    /// <remarks>
    /// The instance answers an empty directory, which is exactly what a real container answers for a
    /// path it has no counterpart for. Without the agreement the run would hand that folder over and
    /// report a clean zero.
    /// </remarks>
    [Fact]
    public async Task AFolderWhoseRootAgreesOnNothingNeverReachesTheListing()
    {
        var (host, _) = await RunAsync(probeHoldsTheSample: false);
        await using var driven = host;

        Assert.DoesNotContain(
            host.Bytes!.Targets, sent => sent.Contains("manualimport", StringComparison.Ordinal));
    }

    private static async Task<(MonitorHost Host, RecordingJobProgress Progress)> RunAsync(
        bool probeHoldsTheSample = true)
    {
        // The listing is composed from the file the library really seeded, so the probe is answered
        // about the path the product really asked about rather than one the case guessed.
        var empty = """{"parent":"/data/","directories":[],"files":[]}""";
        var listing = new[] { empty };

        var bytes = BodyRecordingHandler.AnsweringByPath(path => path switch
        {
            var route when route.EndsWith("/config/mediamanagement", StringComparison.Ordinal)
                => LinksIntoPlace,
            var route when route.EndsWith("/rootfolder", StringComparison.Ordinal)
                => $$"""[{"id":1,"path":"{{InstanceRoot}}","accessible":true}]""",
            var route when route.EndsWith("/filesystem", StringComparison.Ordinal) => listing[0],
            var route when route.EndsWith("/manualimport", StringComparison.Ordinal) => "[]",
            _ => "{}",
        });

        var host = await MonitorHost.CreateAsync(
            bytes: bytes,
            libraryConfig: new CoveConfiguration { CovePaths = [new CovePath { Path = CoveRoot }] });

        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var seeded = await host.SeedStudioFileAsync(studioId, Folder, SampleSize);

        if (probeHoldsTheSample)
        {
            var onInstance = seeded.Replace(CoveRoot, InstanceRoot, StringComparison.Ordinal);
            listing[0] = $$"""
                {"parent":"{{InstanceRoot}}/","directories":[],
                 "files":[{"path":"{{onInstance}}","size":{{SampleSize}}}]}
                """;
        }

        var answered = await host.ReflectOwnedViewAsync("studio", studioId);
        Assert.NotNull(answered.JobId);

        var progress = new RecordingJobProgress();
        await host.Jobs.RunLastAsync(progress, TestCt);
        return (host, progress);
    }
}
