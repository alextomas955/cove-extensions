using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Connection;

/// <summary>
/// The four refusals are what the product promises never to collapse, and three of them are
/// indistinguishable under the wrong test. These drive every step of the decision table, and one of
/// them fails if the table is reordered.
/// </summary>
public sealed class ConnectionFailureClassifierTests
{
    private const string JsonContentType = "application/json; charset=utf-8";

    [Fact]
    public void Step0_NothingSupplied_IsNotConfigured()
        => Assert.Equal(
            ConnectionFailureKind.NotConfigured,
            ConnectionFailureClassifier.Classify(ConnectionObservation.NotConfigured()));

    [Theory]
    [InlineData(ConnectionTransportFailure.NoResponse)]
    [InlineData(ConnectionTransportFailure.Timeout)]
    [InlineData(ConnectionTransportFailure.Tls)]
    public void Step1_EveryTransportFailure_IsUnreachable(ConnectionTransportFailure failure)
        => Assert.Equal(
            ConnectionFailureKind.Unreachable,
            ConnectionFailureClassifier.Classify(ConnectionObservation.TransportFailed(failure)));

    /// <summary>
    /// The ordering assertion. Both generations answer a turned-down key with <c>401</c> and an EMPTY
    /// content-type, so a table that tested content type before status would read this exact input as
    /// an answer from something that is not the API. This test goes red under that reordering and
    /// under no other change.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Step2_UnauthorizedWithNoContentType_IsKeyRejected_NotAWebPage(string? contentType)
        => Assert.Equal(
            ConnectionFailureKind.KeyRejected,
            ConnectionFailureClassifier.Classify(
                ConnectionObservation.Answered(401, contentType, document: null)));

    [Fact]
    public void Step2_Forbidden_FoldsIntoKeyRejected()
        => Assert.Equal(
            ConnectionFailureKind.KeyRejected,
            ConnectionFailureClassifier.Classify(
                ConnectionObservation.Answered(403, contentType: null, document: null)));

    /// <summary>
    /// A wrong address answers <c>200 text/html</c>, so this cannot be reached by a status test. An
    /// empty content-type on a non-401 lands here too, which is the measured shape of a 404 under
    /// <c>/api/v3</c>.
    /// </summary>
    [Theory]
    [InlineData(200, "text/html")]
    [InlineData(200, "text/html; charset=utf-8")]
    [InlineData(404, "")]
    [InlineData(200, null)]
    public void Step3_ANonJsonMediaType_IsNotTheWhisparrApi(int status, string? contentType)
        => Assert.Equal(
            ConnectionFailureKind.NotTheWhisparrApi,
            ConnectionFailureClassifier.Classify(
                ConnectionObservation.Answered(status, contentType, document: null)));

    [Fact]
    public void Step4_JsonThatDidNotParse_IsNotTheWhisparrApi()
        => Assert.Equal(
            ConnectionFailureKind.NotTheWhisparrApi,
            ConnectionFailureClassifier.Classify(
                ConnectionObservation.Answered(
                    200,
                    JsonContentType,
                    WhisparrStatusDocument.Parse("<html><body>not json</body></html>"))));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    public void Step4_JsonCarryingNoVersion_IsNotTheWhisparrApi(string body)
        => Assert.Equal(
            ConnectionFailureKind.NotTheWhisparrApi,
            ConnectionFailureClassifier.Classify(
                ConnectionObservation.Answered(200, JsonContentType, WhisparrStatusDocument.Parse(body))));

    /// <summary>
    /// A problem document parses and is JSON, so it survives steps 3 and 4's first half, and is then
    /// refused for the version it does not have. That is the right answer for it.
    /// </summary>
    [Fact]
    public void Step4_AProblemDocument_ParsesAndThenFailsOnTheAbsentVersion()
    {
        var document = WhisparrStatusDocument.Parse(
            """{"type":"about:blank","title":"Not Found","status":404}""");

        Assert.NotNull(document);
        Assert.Null(document.Version);
        Assert.Equal(
            ConnectionFailureKind.NotTheWhisparrApi,
            ConnectionFailureClassifier.Classify(
                ConnectionObservation.Answered(404, "application/problem+json; charset=utf-8", document)));
    }

    [Theory]
    [InlineData("Radarr")]
    [InlineData("Sonarr")]
    [InlineData("Lidarr")]
    public void Step5_AnotherApplication_IsVersionNotManaged(string appName)
        => Assert.Equal(
            ConnectionFailureKind.VersionNotManaged,
            ConnectionFailureClassifier.Classify(Answered(Document("5.0.0.1", "master", appName))));

    /// <summary>
    /// A negative test, so it cannot mis-refuse a real Whisparr. Case-insensitive because the
    /// comparison must not turn on a spelling this code never measured.
    /// </summary>
    [Theory]
    [InlineData("Whisparr")]
    [InlineData("whisparr")]
    [InlineData("WHISPARR")]
    public void Step5_ThisProductsOwnName_PassesThrough(string appName)
        => Assert.Equal(
            ConnectionFailureKind.Connected,
            ConnectionFailureClassifier.Classify(Answered(Document("3.3.8.1097", "eros", appName))));

    [Theory]
    [InlineData("1.0.0.1")]
    [InlineData("4.0.0.1")]
    [InlineData("not-a-version")]
    public void Step6_AMajorThisProductDoesNotManage_IsVersionNotManaged(string version)
        => Assert.Equal(
            ConnectionFailureKind.VersionNotManaged,
            ConnectionFailureClassifier.Classify(Answered(Document(version, "eros", "Whisparr"))));

    /// <summary>
    /// The branch is the same on both rows on purpose: the classifier reads the version major and
    /// nothing else, and a corroborating reading that disagrees is the detector's finding to report
    /// rather than a reason to refuse.
    /// </summary>
    [Theory]
    [InlineData("3.3.8.1097")]
    [InlineData("2.2.0.231")]
    public void Step7_AManagedMajor_IsConnected(string version)
        => Assert.Equal(
            ConnectionFailureKind.Connected,
            ConnectionFailureClassifier.Classify(Answered(Document(version, "eros", "Whisparr"))));

    /// <summary>
    /// The measured content types, including the form with no space after the semicolon, which a
    /// comparison against the raw header string would miss.
    /// </summary>
    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/json;charset=utf-8", true)]
    [InlineData("APPLICATION/JSON", true)]
    [InlineData("application/problem+json; charset=utf-8", true)]
    [InlineData("text/json", true)]
    [InlineData("text/html", false)]
    [InlineData("text/html; charset=utf-8", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a media type at all", false)]
    public void AMediaTypeIsJudgedOnItsMediaTypeAlone(string? contentType, bool isJson)
        => Assert.Equal(isJson, ConnectionFailureClassifier.IsJsonMediaType(contentType));

    /// <summary>Every kind the table can produce is produced by some input.</summary>
    [Fact]
    public void EveryKindIsReachable()
    {
        var reached = new[]
        {
            ConnectionFailureClassifier.Classify(ConnectionObservation.NotConfigured()),
            ConnectionFailureClassifier.Classify(
                ConnectionObservation.TransportFailed(ConnectionTransportFailure.NoResponse)),
            ConnectionFailureClassifier.Classify(ConnectionObservation.Answered(401, null, null)),
            ConnectionFailureClassifier.Classify(ConnectionObservation.Answered(200, "text/html", null)),
            ConnectionFailureClassifier.Classify(Answered(Document("9.0.0.1", "eros", "Whisparr"))),
            ConnectionFailureClassifier.Classify(Answered(Document("3.3.8.1097", "eros", "Whisparr"))),
        };

        Assert.Equal(Enum.GetValues<ConnectionFailureKind>().Order(), reached.Order());
    }

    private static ConnectionObservation Answered(WhisparrStatusDocument document)
        => ConnectionObservation.Answered(200, JsonContentType, document);

    private static WhisparrStatusDocument Document(string version, string branch, string appName)
        => new(version, branch, appName, true, true, true, true);
}
