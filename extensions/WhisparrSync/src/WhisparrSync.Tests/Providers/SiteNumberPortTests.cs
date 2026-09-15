using WhisparrSync.Contracts;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Providers;

/// <summary>
/// What the Whisparr slice is answered when it asks for the number a site is named by.
/// </summary>
/// <remarks>
/// Driven over a catalogue double rather than a live read: the subject is which of the three
/// answers each catalogue answer becomes, and a read of a real source would settle none of them.
/// </remarks>
public sealed class SiteNumberPortTests
{
    private const string StudioUuid = "e3b61b3e-0c20-4bea-9441-b88430ed6317";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// A library may already hold the number itself, and a read to confirm a number already in hand
    /// is a request paid per studio for nothing.
    /// </summary>
    [Fact]
    public async Task AnIdentifierThatIsAlreadyANumberIsAnsweredWithoutAskingTheSource()
    {
        var catalogue = new NumberingCatalogue(ProviderSiteNumber.NotReached);

        var resolved = await new SiteNumberPort(catalogue).ResolveSiteNumberAsync("3372", TestCt);

        Assert.Equal(3372, resolved.Number);
        Assert.Empty(catalogue.Asked);
    }

    /// <summary>A uuid the source names a site for is answered with that site's number.</summary>
    [Fact]
    public async Task AUuidTheSourceNamesASiteForIsAnsweredWithItsNumber()
    {
        var catalogue = new NumberingCatalogue(ProviderSiteNumber.Numbered(92));

        var resolved = await new SiteNumberPort(catalogue).ResolveSiteNumberAsync(
            StudioUuid, TestCt);

        Assert.Equal(92, resolved.Number);
        Assert.Equal([StudioUuid], catalogue.Asked);
    }

    /// <summary>The source stating it names no site is carried across as that and not as a failure.</summary>
    [Fact]
    public async Task AUuidTheSourceNamesNoSiteForIsAnsweredAsThat()
    {
        var catalogue = new NumberingCatalogue(ProviderSiteNumber.NamesNone);

        var resolved = await new SiteNumberPort(catalogue).ResolveSiteNumberAsync(
            StudioUuid, TestCt);

        Assert.Equal(WhisparrSiteNumber.NamesNone, resolved);
        Assert.True(resolved.WasReached);
    }

    /// <summary>
    /// A read that arrived at nothing stays on its own side of the answer. A rate-limited read is
    /// one of those, and counted as a site the source names none for it would move a studio into
    /// the column of sites this run has established something about.
    /// </summary>
    [Fact]
    public async Task AReadThatNeverArrivedIsHeldApartFromASiteTheSourceNamesNoneFor()
    {
        var catalogue = new NumberingCatalogue(ProviderSiteNumber.NotReached);

        var resolved = await new SiteNumberPort(catalogue).ResolveSiteNumberAsync(
            StudioUuid, TestCt);

        Assert.Equal(WhisparrSiteNumber.NotReached, resolved);
        Assert.NotEqual(WhisparrSiteNumber.NamesNone, resolved);
    }

    /// <summary>
    /// A source issuing no site number at all is a composition fault rather than an answer about
    /// one site. Answered as a site with no number, it would report every studio in the library as
    /// unidentified.
    /// </summary>
    [Fact]
    public async Task ASourceIssuingNoSiteNumberRaisesRatherThanAnsweringAboutTheSite()
    {
        var catalogue = new NumberingCatalogue(ProviderSiteNumber.NamesNone, issuesNumbers: false);

        var raised = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SiteNumberPort(catalogue).ResolveSiteNumberAsync(StudioUuid, TestCt));

        Assert.Contains("site number", raised.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(catalogue.Asked);
    }

    /// <summary>A catalogue answering one prepared site number and recording what it was asked.</summary>
    /// <remarks>
    /// It carries the capability set of a real provider rather than one assembled per case, so a
    /// case cannot ask for a combination no provider has. Every other member of the seam throws:
    /// nothing on this path reads a catalogue page, and a member answering an empty one would let a
    /// pass that reached the wrong seam look like one that found nothing.
    /// </remarks>
    private sealed class NumberingCatalogue : IProviderCatalogue, IResolvesNumericSiteId
    {
        private readonly ProviderSiteNumber _answer;

        internal NumberingCatalogue(ProviderSiteNumber answer, bool issuesNumbers = true)
        {
            _answer = answer;
            Capabilities = issuesNumbers
                ? ProviderCapabilities.ForThePornDb(this)
                : ProviderCapabilities.ForStashDb(this);
        }

        /// <summary>Every site identifier this was asked to resolve, in order.</summary>
        public List<string> Asked { get; } = [];

        public ProviderCapabilitySet Capabilities { get; }

        public IReadOnlyList<ProviderSortOption> Sorts => throw Unasked(nameof(Sorts));

        public string DefaultSort => throw Unasked(nameof(DefaultSort));

        public Task<ProviderSiteNumber> ResolveNumericSiteIdAsync(
            string providerSiteId, CancellationToken ct)
        {
            Asked.Add(providerSiteId);
            return Task.FromResult(_answer);
        }

        public string? SceneAddress(string providerSceneId) => throw Unasked(nameof(SceneAddress));

        public Task<ProviderCatalogueAnswer> ReadPageAsync(
            ProviderCatalogueRequest request, CancellationToken ct)
            => throw Unasked(nameof(ReadPageAsync));

        public Task<int?> ReadCatalogueSizeAsync(
            ProviderCatalogueRequest request, CancellationToken ct)
            => throw Unasked(nameof(ReadCatalogueSizeAsync));

        public Task<ProviderIdentityLookup> LookUpByNameAsync(
            WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
            => throw Unasked(nameof(LookUpByNameAsync));

        public Task<int?> ResolveNumericSceneIdAsync(string providerSceneId, CancellationToken ct)
            => throw Unasked(nameof(ResolveNumericSceneIdAsync));

        public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
            WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
            => throw Unasked(nameof(ListFacetMenusAsync));

        public Task<ProviderFacetSearch> SearchFacetValuesAsync(
            WhisparrEntityKind kind,
            string providerEntityId,
            string facetKey,
            string fragment,
            CancellationToken ct)
            => throw Unasked(nameof(SearchFacetValuesAsync));

        private static InvalidOperationException Unasked(string member)
            => new($"Unexpected read: {member}. Nothing on this path asks for it.");
    }
}
