using System.Net;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// Studios reach this generation carrying a provider's own code, not a number, so the identifier
// the cases ask about is the shape a library actually holds.
public sealed class InstanceSiteNumberPortTests
{
    private const string StudioUuid = "e3b61b3e-0c20-4bea-9441-b88430ed6317";
    private const string SomeKey = "0123456789abcdef0123456789abcdef";

    private static readonly Uri SomeAddress = new("http://whisparr:6969");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // A library may already hold the number, and confirming it would cost a request per studio.
    [Fact]
    public async Task AnIdentifierThatIsAlreadyANumberIsAnsweredWithoutAskingTheInstance()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        using var gateway = new Whisparr2Gateway(() => handler);

        var resolved = await new InstanceSiteNumberPort(gateway)
            .ResolveSiteNumberAsync(
                new WhisparrBinding(WhisparrGeneration.V2, SomeAddress, SomeKey), "3372", TestCt);

        Assert.Equal(3372, resolved.Number);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AUuidTheInstanceNamesASiteForIsAnsweredWithItsNumber()
    {
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, """[{"title":"Some Site","tvdbId":92}]""");
        using var gateway = new Whisparr2Gateway(() => handler);

        var resolved = await new InstanceSiteNumberPort(gateway)
            .ResolveSiteNumberAsync(
                new WhisparrBinding(WhisparrGeneration.V2, SomeAddress, SomeKey), StudioUuid, TestCt);

        Assert.Equal(92, resolved.Number);
        Assert.Contains(StudioUuid, Assert.Single(handler.Targets), StringComparison.Ordinal);
    }

    // An instance answering no site states an absence about the studio, not a failure.
    [Fact]
    public async Task AUuidTheInstanceNamesNoSiteForIsAnsweredAsThat()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        using var gateway = new Whisparr2Gateway(() => handler);

        var resolved = await new InstanceSiteNumberPort(gateway)
            .ResolveSiteNumberAsync(
                new WhisparrBinding(WhisparrGeneration.V2, SomeAddress, SomeKey), StudioUuid, TestCt);

        Assert.Equal(WhisparrSiteNumber.NamesNone, resolved);
        Assert.True(resolved.WasReached);
    }

    // A read that never arrived must stay apart from a site the instance names none for. Merged,
    // it would record the studio as settled.
    [Fact]
    public async Task AReadThatWasRefusedIsHeldApartFromASiteTheInstanceNamesNoneFor()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.ServiceUnavailable, string.Empty);
        using var gateway = new Whisparr2Gateway(() => handler);

        var resolved = await new InstanceSiteNumberPort(gateway)
            .ResolveSiteNumberAsync(
                new WhisparrBinding(WhisparrGeneration.V2, SomeAddress, SomeKey), StudioUuid, TestCt);

        Assert.Equal(WhisparrSiteNumber.NotReached, resolved);
        Assert.NotEqual(WhisparrSiteNumber.NamesNone, resolved);
    }

    // An entry carrying no number names no site to address. Answered as not reached, a studio the
    // instance has already settled would be asked about again on every run.
    [Fact]
    public async Task AnEntryCarryingNoNumberIsAnsweredAsNamingNoSite()
    {
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, """[{"title":"Some Site"}]""");
        using var gateway = new Whisparr2Gateway(() => handler);

        var resolved = await new InstanceSiteNumberPort(gateway)
            .ResolveSiteNumberAsync(
                new WhisparrBinding(WhisparrGeneration.V2, SomeAddress, SomeKey), StudioUuid, TestCt);

        Assert.Equal(WhisparrSiteNumber.NamesNone, resolved);
    }
}
