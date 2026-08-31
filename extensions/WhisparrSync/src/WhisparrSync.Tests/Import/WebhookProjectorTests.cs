using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// What each generation's real import delivery is read as, run against the bodies those instances
/// really sent.
/// </summary>
/// <remarks>
/// The two fixtures are INPUTS, taken verbatim from a delivery each build made. Every expected value
/// below is transcribed by hand from those files rather than read back out of them, because an
/// expectation computed from the document it checks agrees with it whatever either says.
/// <para>
/// The generations carry different key sets for the same event, which is why the reader is told the
/// generation instead of inferring one from the body: a body's own keys would classify a v2 delivery
/// as an unrecognised v3 one.
/// </para>
/// </remarks>
public sealed class WebhookProjectorTests
{
    private const string V3Capture = "whisparr-v3-3.3.8.1097-webhook-import.json";
    private const string V2Capture = "whisparr-v2-2.2.0.231-webhook-import.json";

    /// <summary>The event type both generations' import delivery carried, transcribed by hand.</summary>
    private const string DownloadEventType = "Download";

    [Fact]
    public void TheV3CaptureReadsAsTheFileAndTheSceneItNamed()
    {
        var reading = WebhookProjector.Read(WhisparrGeneration.V3, Captured(V3Capture));

        Assert.Equal(WebhookProjectionOutcome.Projected, reading.Outcome);
        Assert.Equal(DownloadEventType, reading.EventType);

        var candidate = Assert.IsType<ImportCandidate>(reading.Candidate);
        Assert.Equal(WhisparrGeneration.V3, candidate.Generation);
        Assert.Equal(
            "/probe-root/scenes/Brazzers/Brazzers Exxtra/2025-07-15 - Brazzers Beach "
                + "[1703a150-ceec-4953-ac10-d7ebc7d0974f]/Cove.Probe.Row15.2024.1080p.WEB-DL.mp4",
            candidate.ReportedPath);
        Assert.Equal(3102, candidate.ReportedSize);
        Assert.Equal("1703a150-ceec-4953-ac10-d7ebc7d0974f", candidate.RemoteId);
    }

    [Fact]
    public void TheV2CaptureReadsAsTheFileAndTheSceneItNamed()
    {
        var reading = WebhookProjector.Read(WhisparrGeneration.V2, Captured(V2Capture));

        Assert.Equal(WebhookProjectionOutcome.Projected, reading.Outcome);
        Assert.Equal(DownloadEventType, reading.EventType);

        var candidate = Assert.IsType<ImportCandidate>(reading.Candidate);
        Assert.Equal(WhisparrGeneration.V2, candidate.Generation);
        Assert.Equal(
            "/probe-root/Teen/Cove.Probe.Row15.S01E01.1080p.WEB-DL.mp4", candidate.ReportedPath);
        Assert.Equal(3102, candidate.ReportedSize);
        // Carried as a JSON number on this generation and as a string on the other. It reaches the
        // core in one form so a later reader has one type to handle.
        Assert.Equal("4149372", candidate.RemoteId);
    }

    /// <summary>
    /// A generation's own capture read as the OTHER generation produces no path.
    /// </summary>
    /// <remarks>
    /// The discriminating control for the two cases above: without it, a reader that looked in both
    /// places would pass them, and the per-generation rule would be untested.
    /// </remarks>
    [Fact]
    public void ACaptureReadAsTheWrongGenerationFindsNoPath()
    {
        Assert.Equal(
            WebhookProjectionOutcome.NoReadablePath,
            WebhookProjector.Read(WhisparrGeneration.V2, Captured(V3Capture)).Outcome);
        Assert.Equal(
            WebhookProjectionOutcome.NoReadablePath,
            WebhookProjector.Read(WhisparrGeneration.V3, Captured(V2Capture)).Outcome);
    }

    /// <summary>
    /// Every event type other than the measured one is ignored, on both generations.
    /// </summary>
    /// <remarks>
    /// Only the import delivery has been captured, so these spellings are not claimed to be the ones
    /// an instance sends. What is fixed here is the rule that decides them: an event type this
    /// product has not measured is ignored rather than acted on, whatever it is called.
    /// </remarks>
    [Theory]
    [InlineData("Grab")]
    [InlineData("Rename")]
    [InlineData("MovieFileDelete")]
    [InlineData("EpisodeFileDelete")]
    [InlineData("Test")]
    [InlineData("download")]
    public void AnEventTypeOtherThanTheMeasuredOneIsIgnored(string eventType)
    {
        foreach (var generation in new[] { WhisparrGeneration.V3, WhisparrGeneration.V2 })
        {
            var body = Captured(generation == WhisparrGeneration.V3 ? V3Capture : V2Capture);
            body["eventType"] = eventType;

            var reading = WebhookProjector.Read(generation, body);

            Assert.Equal(WebhookProjectionOutcome.Ignored, reading.Outcome);
            Assert.Equal(eventType, reading.EventType);
            Assert.Null(reading.Candidate);
        }
    }

    /// <summary>
    /// An act-list event carrying no readable path is a named refusal, never a silent ignore.
    /// </summary>
    /// <remarks>
    /// An event this product handles whose body it did not understand is a different fact from an
    /// event it does not handle, and reporting them alike would hide the first.
    /// </remarks>
    [Fact]
    public void AnActListEventWithNoReadablePathIsItsOwnRefusal()
    {
        var body = Captured(V3Capture);
        body.Remove("movieFile");

        var reading = WebhookProjector.Read(WhisparrGeneration.V3, body);

        Assert.Equal(WebhookProjectionOutcome.NoReadablePath, reading.Outcome);
        Assert.Equal(DownloadEventType, reading.EventType);
        Assert.Null(reading.Candidate);
    }

    [Fact]
    public void ABodyNamingNoEventTypeIsUnreadable()
    {
        var body = Captured(V3Capture);
        body.Remove("eventType");

        Assert.Equal(
            WebhookProjectionOutcome.Unreadable,
            WebhookProjector.Read(WhisparrGeneration.V3, body).Outcome);
        Assert.Equal(
            WebhookProjectionOutcome.Unreadable,
            WebhookProjector.Read(WhisparrGeneration.V3, null).Outcome);
    }

    /// <summary>A body carrying an act-list event and a blank path names no path.</summary>
    [Fact]
    public void ABlankPathIsNoPath()
    {
        var body = Captured(V3Capture);
        ((JsonObject)body["movieFile"]!)["path"] = "   ";

        Assert.Equal(
            WebhookProjectionOutcome.NoReadablePath,
            WebhookProjector.Read(WhisparrGeneration.V3, body).Outcome);
    }

    /// <summary>A delivery carrying an act-list event and no scene identifier still projects.</summary>
    /// <remarks>
    /// A scene the instance has not identified has no shared identifier to carry, and the file is
    /// still one to register. Matching is a later step, and the absence is what tells it so.
    /// </remarks>
    [Fact]
    public void ADeliveryCarryingNoRemoteIdentifierStillProjects()
    {
        var body = Captured(V3Capture);
        ((JsonObject)body["movie"]!).Remove("stashId");

        var reading = WebhookProjector.Read(WhisparrGeneration.V3, body);

        Assert.Equal(WebhookProjectionOutcome.Projected, reading.Outcome);
        Assert.Null(reading.Candidate!.RemoteId);
    }

    /// <summary>
    /// Which generation sent a delivery, from the user agent an inbound consumer sees first.
    /// </summary>
    /// <remarks>
    /// Both agent strings are transcribed by hand from the deliveries the two builds made.
    /// </remarks>
    [Theory]
    [InlineData("Whisparr/3.3.8.1097 (alpine 3.23.5)", WhisparrGeneration.V3)]
    [InlineData("Whisparr/2.2.0.231 (alpine 3.23.5)", WhisparrGeneration.V2)]
    public void TheUserAgentNamesTheGeneration(string userAgent, WhisparrGeneration expected)
        => Assert.Equal(expected, WebhookProjector.GenerationOf(userAgent));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curl/8.5.0")]
    [InlineData("Whisparr/")]
    [InlineData("Whisparr/9.0.0 (alpine 3.23.5)")]
    public void AUserAgentThisProductDoesNotManageNamesNoGeneration(string? userAgent)
        => Assert.Null(WebhookProjector.GenerationOf(userAgent));

    /// <summary>One captured delivery body, freshly parsed so a mutating case cannot affect another.</summary>
    private static JsonObject Captured(string fileName)
        => Assert.IsType<JsonObject>(JsonNode.Parse(ProbeFixtures.Read(fileName)));
}
