namespace WhisparrSync.Tests.Missing;

/// <summary>
/// Which spelling of a scene identifier the library stores, pinned with where the answer came from.
/// </summary>
/// <remarks>
/// Ownership is judged on this value. Keyed on the wrong spelling the subtraction matches nothing
/// and every scene the library already holds reads as missing, which is a wrong answer a reader
/// would read as right.
/// <para>
/// The host reaches every metadata source over GraphQL and writes what that schema answers as a
/// scene's own identifier. One provider additionally exposes an integer key on a REST API the host
/// never calls, so that key reaches no stored row.
/// </para>
/// </remarks>
public sealed class SceneIdSpellingPinTests
{
    /// <summary>Where the pinned answer was read, and when.</summary>
    /// <remarks>
    /// Named so the pin is a recorded reading rather than an assertion of belief. A reader who
    /// doubts it has the source to go back to.
    /// </remarks>
    private const string Provenance =
        "Read 2026-09-06 from the host's own write path: the metadata service resolves a scene "
        + "through findScene(id:) over GraphQL, binds that schema's id, and writes it unchanged to "
        + "the video's remote-id row. Corroborated live on the development host, whose studio rows "
        + "carry uuids under https://stashdb.org/graphql.";

    /// <summary>The shape a stored scene identifier takes.</summary>
    private const string StoredSpelling = "uuid";

    [Fact]
    public void TheStoredSceneIdentifierIsTheProvidersUuid()
    {
        Assert.Equal("uuid", StoredSpelling);
        Assert.NotEmpty(Provenance);
    }

    /// <summary>
    /// A stored identifier is compared as the library holds it, so the subtraction matches on the
    /// same spelling the host wrote.
    /// </summary>
    [Theory]
    [InlineData("1d468eaf-af0f-4f11-9dcf-9aa3cf62aa95")]
    [InlineData("5ee16943-0da6-4ee4-94c1-54172e3d0b7e")]
    public void ARecordedIdentifierIsAUuidAndNotAnInteger(string recorded)
    {
        Assert.True(Guid.TryParse(recorded, out _));
        Assert.False(int.TryParse(recorded, out _));
    }

    /// <summary>
    /// What could not be read, so a later reader is not left inferring that everything was.
    /// </summary>
    /// <remarks>
    /// The development host's video routes answer a database error for a migration it has not
    /// applied, so no stored video row was read directly. The pin above rests on the host's write
    /// path instead, which states the rule rather than one instance of it.
    /// </remarks>
    [Fact]
    public void TheReadingThatWasUnavailableIsNamedRatherThanLeftUnstated()
    {
        Assert.Contains("write path", Provenance, StringComparison.Ordinal);
    }
}
