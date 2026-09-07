namespace WhisparrSync.Adapters;

/// <summary>
/// What the connected instance's own API description says it can answer, decided once per connection and read
/// by <see cref="AdapterSelector"/> to choose which v3 adapter to build.
/// </summary>
/// <remarks>
/// <para>
/// Internal only — this never reaches the wire and carries no serialization contract.
/// </para>
/// <para>
/// A reference type rather than a value one because the memo slot it is cached in holds reference values;
/// equality is still by member.
/// </para>
/// </remarks>
internal sealed record WhisparrCapabilities(bool NarrowEntityCatalogue)
{
    // The two route keys, spelled exactly as the instance's document spells them
    // (e2e/fixtures/wire/openapi-capability.json, read off 3.3.4.794). Compared case-insensitively because
    // Servarr routes are.
    private const string StudioCatalogueRoute = "/api/v3/movie/listbystudioforeignid";
    private const string PerformerCatalogueRoute = "/api/v3/movie/listbyperformerforeignid";

    /// <summary>The fail-closed answer: every capability absent, so every caller defers.</summary>
    /// <remarks>
    /// This is what a document read that did not answer Ok resolves to. Fail-closed means a classified refusal
    /// at the caller — never a wider read.
    /// </remarks>
    internal static WhisparrCapabilities None { get; } = new(NarrowEntityCatalogue: false);

    /// <summary>
    /// Maps a document's declared path keys to the capabilities they support.
    /// </summary>
    /// <remarks>
    /// The narrow-catalogue capability requires BOTH sibling routes: one role member answers for a studio and
    /// for a performer, so an instance declaring only one of the two cannot honor it and must not be handed an
    /// adapter that claims to.
    /// </remarks>
    internal static WhisparrCapabilities From(IEnumerable<string> declaredPaths)
    {
        var studio = false;
        var performer = false;
        foreach (var path in declaredPaths)
        {
            studio = studio || string.Equals(path, StudioCatalogueRoute, StringComparison.OrdinalIgnoreCase);
            performer = performer || string.Equals(path, PerformerCatalogueRoute, StringComparison.OrdinalIgnoreCase);
        }

        return studio && performer ? new WhisparrCapabilities(NarrowEntityCatalogue: true) : None;
    }
}
