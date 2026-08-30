using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Connection;

/// <summary>
/// The detector decides on the version major alone, and reports what the branch and the count fields
/// say separately. A build whose corroborating readings contradict its own version is the build gap
/// the product must distinguish from a generation gap, so it is reported and never resolved.
/// </summary>
public sealed class GenerationDetectorTests
{
    [Theory]
    [InlineData("3.3.8.1097", WhisparrGeneration.V3)]
    [InlineData("3.0.0.1", WhisparrGeneration.V3)]
    [InlineData("2.2.0.231", WhisparrGeneration.V2)]
    [InlineData("2.0.0.1", WhisparrGeneration.V2)]
    public void AManagedMajor_DecidesTheGeneration(string version, WhisparrGeneration expected)
        => Assert.Equal(expected, GenerationDetector.Detect(Document(version, "eros", counts: true)).Generation);

    /// <summary>
    /// A major this product does not manage yields no generation. The detector never picks the
    /// nearest one it does have: an adapter chosen by guess would report a library it never read.
    /// </summary>
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

    /// <summary>The version found travels on the reading, which is what lets a refusal name it.</summary>
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

    /// <summary>
    /// The build-gap reading. Each row is a document whose branch or count fields contradict its own
    /// version major. The generation is still decided, because the version major is the decision, and
    /// the disagreement is REPORTED rather than allowed to overturn it or to be averaged away.
    /// </summary>
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

    /// <summary>Both readings travel on the result, so a caller can say WHICH one disagreed.</summary>
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
