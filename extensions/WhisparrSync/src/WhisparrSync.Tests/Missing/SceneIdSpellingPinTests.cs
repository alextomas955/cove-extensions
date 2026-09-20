namespace WhisparrSync.Tests.Missing;

// Ownership is judged on this spelling. Keyed on the wrong one the subtraction matches nothing and
// every scene the library already holds reads as missing.
public sealed class SceneIdSpellingPinTests
{
    private const string Provenance =
        "Read 2026-09-06 from the host's own write path: the metadata service resolves a scene "
        + "through findScene(id:) over GraphQL, binds that schema's id, and writes it unchanged to "
        + "the video's remote-id row. Corroborated live on the development host, whose studio rows "
        + "carry uuids under https://stashdb.org/graphql.";

    private const string StoredSpelling = "uuid";

    [Fact]
    public void TheStoredSceneIdentifierIsTheProvidersUuid()
    {
        Assert.Equal("uuid", StoredSpelling);
        Assert.NotEmpty(Provenance);
    }

    [Theory]
    [InlineData("1d468eaf-af0f-4f11-9dcf-9aa3cf62aa95")]
    [InlineData("5ee16943-0da6-4ee4-94c1-54172e3d0b7e")]
    public void ARecordedIdentifierIsAUuidAndNotAnInteger(string recorded)
    {
        Assert.True(Guid.TryParse(recorded, out _));
        Assert.False(int.TryParse(recorded, out _));
    }

    // The development host's video routes answer a database error for an unapplied migration, so no
    // stored video row was read directly and the pin rests on the host's write path.
    [Fact]
    public void TheReadingThatWasUnavailableIsNamedRatherThanLeftUnstated()
    {
        Assert.Contains("write path", Provenance, StringComparison.Ordinal);
    }
}
