using WhisparrSync.Connection;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Connection;

// Every expected value here was transcribed by hand from a response a live instance produced, and
// each names the build it came from. The captured documents are inputs; no expectation is read out
// of one. The fixture ledger they came from is local-only and unversioned, so the facts live here.
public sealed class WhisparrStatusPinTests
{
    // Transcribed by hand from the two builds this extension was measured against.
    private const string V3Build = "3.3.8.1097";
    private const string V2Build = "2.2.0.231";

    private const string V3StatusFixture = "whisparr-v3-3.3.8.1097-system-status.json";
    private const string V2StatusFixture = "whisparr-v2-2.2.0.231-system-status.json";

    [Fact]
    public void V3_ReportsItsVersionAndBranchAndAllFourCountFields()
    {
        var document = Read(V3StatusFixture);

        Assert.Equal("3.3.8.1097", document.Version);
        Assert.Equal("eros", document.Branch);
        Assert.True(document.MovieCountPresent, $"movieCount is absent on {V3Build}");
        Assert.True(document.SceneCountPresent, $"sceneCount is absent on {V3Build}");
        Assert.True(document.PerformerCountPresent, $"performerCount is absent on {V3Build}");
        Assert.True(document.StudioCountPresent, $"studioCount is absent on {V3Build}");
    }

    // Read from the parsed shape, never from a status code. A status-only probe recorded four v2
    // documents that do not exist, so a status is not evidence about this generation.
    [Fact]
    public void V2_ReportsItsVersionAndBranchAndNoneOfTheFourCountFields()
    {
        var document = Read(V2StatusFixture);

        Assert.Equal("2.2.0.231", document.Version);
        Assert.Equal("v2", document.Branch);
        Assert.True(document.NoCountFieldsPresent, $"a count field is present on {V2Build}");
    }

    // appName reads the same on both builds. It tells Whisparr from not-Whisparr and says nothing
    // about the generation.
    [Fact]
    public void AppName_ReadsTheSameOnBothBuilds_AndSoDiscriminatesNothing()
    {
        var v3 = Read(V3StatusFixture);
        var v2 = Read(V2StatusFixture);

        Assert.Equal("Whisparr", v3.AppName);
        Assert.Equal("Whisparr", v2.AppName);
        Assert.Equal(v3.AppName, v2.AppName);
    }

    [Fact]
    public void EachCapturedDocumentDetectsItsOwnGenerationAndCorroboratesIt()
    {
        var v3 = GenerationDetector.Detect(Read(V3StatusFixture));
        Assert.Equal(Contracts.WhisparrGeneration.V3, v3.Generation);
        Assert.True(v3.Corroborated, $"branch and count fields do not corroborate v3 on {V3Build}");

        var v2 = GenerationDetector.Detect(Read(V2StatusFixture));
        Assert.Equal(Contracts.WhisparrGeneration.V2, v2.Generation);
        Assert.True(v2.Corroborated, $"branch and count fields do not corroborate v2 on {V2Build}");
    }

    // On both builds a good key answers with a JSON media type and a rejected key answers with no
    // content type at all. The second is why the classifier tests status before content type.
    [Fact]
    public void TheTwoMeasuredContentTypes_ClassifyAsTheyDidWhenMeasured()
    {
        // Good key, both builds: 200 with a JSON media type.
        Assert.True(
            ConnectionFailureClassifier.IsJsonMediaType("application/json; charset=utf-8"),
            $"the good-key content type measured on {V3Build} and {V2Build} is not read as JSON");

        // Rejected key, both builds: 401 with an empty content type and no body.
        Assert.Equal(
            Contracts.ConnectionFailureKind.KeyRejected,
            ConnectionFailureClassifier.Classify(
                ConnectionObservation.Answered(401, string.Empty, WhisparrStatusDocument.Parse(string.Empty))));
    }

    // An unknown path at the site root answers 200 text/html on both builds, so a web page is
    // detected on the content type. The status says nothing.
    [Fact]
    public void AnUnknownRootPath_AnswersAsAWebPage_AndIsRefusedOnItsContentType()
    {
        var observation = ConnectionObservation.Answered(
            200,
            "text/html",
            WhisparrStatusDocument.Parse("<!DOCTYPE html><html lang=\"en\"><body></body></html>"));

        Assert.Equal(
            Contracts.ConnectionFailureKind.NotTheWhisparrApi,
            ConnectionFailureClassifier.Classify(observation));
    }

    private static WhisparrStatusDocument Read(string fixtureName)
    {
        var document = WhisparrStatusDocument.Parse(ProbeFixtures.Read(fixtureName));
        Assert.NotNull(document);
        return document;
    }
}
