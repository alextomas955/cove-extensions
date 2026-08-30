using WhisparrSync.Connection;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Connection;

/// <summary>
/// The external facts the decision table rests on, pinned.
/// </summary>
/// <remarks>
/// Every expected value below was TRANSCRIBED BY HAND from a response an instance produced, and each
/// names the build it came from. An expectation computed from the document it checks would agree with
/// that document whatever either said; these go red when a build changes, which is the whole reason
/// they exist. The fixture ledger they were taken from is local-only and unversioned, so a fact this
/// code depends on has to live here to survive.
/// <para>
/// The captured documents are INPUTS. Nothing here reads an expectation out of one.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Taken on the parsed SHAPE, never on a status code. A status-only probe already recorded four
    /// v2 documents that do not exist, so a status is not evidence about this generation.
    /// </summary>
    [Fact]
    public void V2_ReportsItsVersionAndBranchAndNoneOfTheFourCountFields()
    {
        var document = Read(V2StatusFixture);

        Assert.Equal("2.2.0.231", document.Version);
        Assert.Equal("v2", document.Branch);
        Assert.True(document.NoCountFieldsPresent, $"a count field is present on {V2Build}");
    }

    /// <summary>
    /// <c>appName</c> reads the same on both, so it discriminates nothing about the generation. It is
    /// exactly right for telling Whisparr from not-Whisparr, and useless for anything else.
    /// </summary>
    [Fact]
    public void AppName_ReadsTheSameOnBothBuilds_AndSoDiscriminatesNothing()
    {
        var v3 = Read(V3StatusFixture);
        var v2 = Read(V2StatusFixture);

        Assert.Equal("Whisparr", v3.AppName);
        Assert.Equal("Whisparr", v2.AppName);
        Assert.Equal(v3.AppName, v2.AppName);
    }

    /// <summary>
    /// The generation each captured document detects as, and whether its two corroborating readings
    /// agree with that. Both builds corroborate; a build where they did not would be the build gap
    /// the detector exists to report.
    /// </summary>
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

    /// <summary>
    /// The two content types the classifier's steps 2 and 3 turn on, transcribed rather than
    /// re-measured here: a good key answers with a JSON media type and a turned-down key answers with
    /// no content type at all, on BOTH builds. The second is why status is tested first, and this
    /// asserts the classifier agrees with the shape that made that ordering necessary.
    /// </summary>
    [Fact]
    public void TheTwoMeasuredContentTypes_ClassifyAsTheyDidWhenMeasured()
    {
        // Good key, both builds: 200 with a JSON media type.
        Assert.True(
            ConnectionFailureClassifier.IsJsonMediaType("application/json; charset=utf-8"),
            $"the good-key content type measured on {V3Build} and {V2Build} is not read as JSON");

        // Turned-down key, both builds: 401 with an EMPTY content type and no body.
        Assert.Equal(
            Contracts.ConnectionFailureKind.KeyRejected,
            ConnectionFailureClassifier.Classify(
                ConnectionObservation.Answered(401, string.Empty, WhisparrStatusDocument.Parse(string.Empty))));
    }

    /// <summary>
    /// An unknown path at the site root answers <c>200 text/html</c> on both builds. That is why
    /// "answered as a web page" is a content-type test: nothing about the status says so.
    /// </summary>
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
