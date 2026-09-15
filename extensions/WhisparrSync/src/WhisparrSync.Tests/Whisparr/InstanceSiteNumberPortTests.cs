using System.Net;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// What the Whisparr slice is answered when it asks for the number a site is named by.
/// </summary>
/// <remarks>
/// Driven over a stub handler rather than a live instance: the subject is which of the three answers
/// each shape of reply becomes, and a read of a real instance would settle none of them.
/// <para>
/// The identifier asked about is the shape a library actually holds. Studios reach this generation
/// carrying a provider's own code, not a number, so a case asking only about numbers would leave the
/// path every real studio takes untested.
/// </para>
/// </remarks>
public sealed class InstanceSiteNumberPortTests
{
    private const string StudioUuid = "e3b61b3e-0c20-4bea-9441-b88430ed6317";
    private const string SomeKey = "0123456789abcdef0123456789abcdef";

    private static readonly Uri SomeAddress = new("http://whisparr:6969");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// A library may already hold the number itself, and a lookup to confirm a number already in
    /// hand is a request paid per studio for nothing.
    /// </summary>
    [Fact]
    public async Task AnIdentifierThatIsAlreadyANumberIsAnsweredWithoutAskingTheInstance()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        using var gateway = new Whisparr2Gateway(() => handler);

        var resolved = await new InstanceSiteNumberPort(gateway)
            .ResolveSiteNumberAsync(SomeAddress, SomeKey, "3372", TestCt);

        Assert.Equal(3372, resolved.Number);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The identifier the library holds is what the instance is asked about, and the number it
    /// answers is the site's own. This is the path every studio held under a provider's code takes.
    /// </summary>
    [Fact]
    public async Task AUuidTheInstanceNamesASiteForIsAnsweredWithItsNumber()
    {
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, """[{"title":"Some Site","tvdbId":92}]""");
        using var gateway = new Whisparr2Gateway(() => handler);

        var resolved = await new InstanceSiteNumberPort(gateway)
            .ResolveSiteNumberAsync(SomeAddress, SomeKey, StudioUuid, TestCt);

        Assert.Equal(92, resolved.Number);
        Assert.Contains(StudioUuid, Assert.Single(handler.Targets), StringComparison.Ordinal);
    }

    /// <summary>
    /// The instance answering no site is carried across as that and not as a failure. It states an
    /// absence, which is something established about the studio.
    /// </summary>
    [Fact]
    public async Task AUuidTheInstanceNamesNoSiteForIsAnsweredAsThat()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        using var gateway = new Whisparr2Gateway(() => handler);

        var resolved = await new InstanceSiteNumberPort(gateway)
            .ResolveSiteNumberAsync(SomeAddress, SomeKey, StudioUuid, TestCt);

        Assert.Equal(WhisparrSiteNumber.NamesNone, resolved);
        Assert.True(resolved.WasReached);
    }

    /// <summary>
    /// A read that arrived at nothing stays on its own side of the answer. Counted as a site the
    /// instance names none for, it would move a studio into the column of sites this run has
    /// established something about.
    /// </summary>
    [Fact]
    public async Task AReadThatWasRefusedIsHeldApartFromASiteTheInstanceNamesNoneFor()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.ServiceUnavailable, string.Empty);
        using var gateway = new Whisparr2Gateway(() => handler);

        var resolved = await new InstanceSiteNumberPort(gateway)
            .ResolveSiteNumberAsync(SomeAddress, SomeKey, StudioUuid, TestCt);

        Assert.Equal(WhisparrSiteNumber.NotReached, resolved);
        Assert.NotEqual(WhisparrSiteNumber.NamesNone, resolved);
    }

    /// <summary>
    /// An entry carrying no number of its own names no site to address, and is not a read that
    /// failed. Answered as not reached, a studio the instance has already settled would be asked
    /// about again on every run.
    /// </summary>
    [Fact]
    public async Task AnEntryCarryingNoNumberIsAnsweredAsNamingNoSite()
    {
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, """[{"title":"Some Site"}]""");
        using var gateway = new Whisparr2Gateway(() => handler);

        var resolved = await new InstanceSiteNumberPort(gateway)
            .ResolveSiteNumberAsync(SomeAddress, SomeKey, StudioUuid, TestCt);

        Assert.Equal(WhisparrSiteNumber.NamesNone, resolved);
    }
}
