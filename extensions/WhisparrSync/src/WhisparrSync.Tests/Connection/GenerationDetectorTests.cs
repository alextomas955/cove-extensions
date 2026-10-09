using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Connection;

public sealed class GenerationDetectorTests
{
    [Theory]
    [InlineData("3.3.8.1097", WhisparrGeneration.V3)]
    [InlineData("3.0.0.1", WhisparrGeneration.V3)]
    [InlineData("2.2.0.231", WhisparrGeneration.V2)]
    [InlineData("2.0.0.1", WhisparrGeneration.V2)]
    public void AManagedMajor_DecidesTheGeneration(string version, WhisparrGeneration expected)
        => Assert.Equal(expected, GenerationDetector.Detect(Document(version, "eros", counts: true)).Generation);

    // The detector never falls back to the nearest managed major. An adapter chosen by guess would
    // report a library it never read.
    [Theory]
    [InlineData("1.0.0.1")]
    [InlineData("4.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("v3")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnmanagedMajor_YieldsNoGenerationAndNothingToCorroborate(string? version)
    {
        var reading = GenerationDetector.Detect(Document(version, "eros", counts: true));

        Assert.Null(reading.Generation);
        Assert.Null(reading.Corroborated);
    }

    [Fact]
    public void TheVersionFoundIsCarriedVerbatim()
        => Assert.Equal("9.9.9.9999", GenerationDetector.Detect(Document("9.9.9.9999", "eros", true)).Version);

    [Fact]
    public void NoDocumentAtAll_YieldsNoGeneration()
    {
        var reading = GenerationDetector.Detect(null);

        Assert.Null(reading.Generation);
        Assert.Null(reading.Version);
        Assert.Null(reading.Corroborated);
    }

    [Theory]
    [InlineData("eros")]
    [InlineData("Eros")]
    public void V3_CorroboratesOnItsBranchAndAllFourCountFields(string branch)
        => Assert.True(GenerationDetector.Detect(Document("3.3.8.1097", branch, counts: true)).Corroborated);

    [Fact]
    public void V2_CorroboratesOnItsBranchAndTheAbsenceOfTheCountFields()
        => Assert.True(GenerationDetector.Detect(Document("2.2.0.231", "v2", counts: false)).Corroborated);

    // Each row's branch or count fields contradict its own version major. The version major still
    // decides the generation, and the disagreement is reported rather than overturning it.
    [Theory]
    [InlineData("3.3.8.1097", "v2", true, WhisparrGeneration.V3)]
    [InlineData("3.3.8.1097", "eros", false, WhisparrGeneration.V3)]
    [InlineData("2.2.0.231", "eros", false, WhisparrGeneration.V2)]
    [InlineData("2.2.0.231", "v2", true, WhisparrGeneration.V2)]
    public void ADisagreementIsReportedAndDoesNotOverturnTheDecision(
        string version,
        string branch,
        bool counts,
        WhisparrGeneration expected)
    {
        var reading = GenerationDetector.Detect(Document(version, branch, counts));

        Assert.Equal(expected, reading.Generation);
        Assert.False(reading.Corroborated, "a contradicting document was reported as corroborated");
    }

    // Both readings travel on the result, so a caller can name which one disagreed.
    [Fact]
    public void TheCorroboratingReadingsThemselvesAreCarried()
    {
        var reading = GenerationDetector.Detect(Document("3.3.8.1097", "v2", counts: false));

        Assert.Equal("v2", reading.Branch);
        Assert.False(reading.CountFieldsPresent);
    }

    private static WhisparrStatusDocument Document(string? version, string branch, bool counts)
        => new(version, branch, "Whisparr", counts, counts, counts, counts);
}
