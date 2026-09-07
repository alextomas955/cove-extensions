using System.Net;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Providers;

/// <summary>
/// The rule every shipped catalogue answers to: a read that did not arrive carries no page.
/// </summary>
/// <remarks>
/// A catalogue added here later belongs in <see cref="Shipped"/>, and the last case reports one that
/// is not. Each provider answered an empty page for a failure independently of the other, so the
/// rule is stated once over all of them rather than case by case.
/// </remarks>
public sealed class ProviderFailureAnswerTests
{
    private const string StashDbSpelling = "https://stashdb.org/graphql";
    private const string ThePornDbSpelling = "https://theporndb.net/graphql";
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private static readonly Dictionary<string, Func<HttpMessageHandler, IProviderCatalogue>>
        Shipped = new(StringComparer.Ordinal)
        {
            [StashDbCatalogue.ProviderName] = handler => new StashDbCatalogue(
                new HttpClient(handler) { BaseAddress = new Uri(StashDbSpelling) },
                new ProviderEndpointPort(Configured(StashDbSpelling)),
                new OptionsStore(new FakeStore()),
                new ProviderPacer(),
                NullLogger.Instance),
            [ThePornDbCatalogue.ProviderName] = handler => new ThePornDbCatalogue(
                new HttpClient(handler),
                new ProviderEndpointPort(Configured(ThePornDbSpelling)),
                new OptionsStore(new FakeStore()),
                new ProviderPacer(),
                NullLogger.Instance),
        };

    public static TheoryData<string> ShippedCatalogues => [.. Shipped.Keys];

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// A refused credential answers no page, whichever provider is configured. An empty page here
    /// is a catalogue listing nothing, which the surface states as a reader owning everything.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedCatalogues))]
    public async Task NoShippedCatalogueAnswersAPageWhereTheProviderRefused(string provider)
    {
        var catalogue = Shipped[provider](
            BodyRecordingHandler.Answering(HttpStatusCode.Unauthorized, "{}"));

        var answer = await catalogue.ReadPageAsync(TagPage(), TestCt);

        Assert.Null(answer.Page);
    }

    /// <summary>Every catalogue the extension ships is covered by the rule above.</summary>
    /// <remarks>
    /// A catalogue is a type that reads over a client of its own, which is what separates one from
    /// the selector that stands in front of them and holds none.
    /// </remarks>
    [Fact]
    public void EveryShippedCatalogueIsNamedHere()
    {
        var covered = Shipped.Values
            .Select(build => build(BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}")).GetType())
            .ToHashSet();

        var declared = typeof(IProviderCatalogue).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && typeof(IProviderCatalogue).IsAssignableFrom(type)
                && type.GetConstructors(
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic)
                    .Any(
                        constructor => constructor.GetParameters()
                            .Any(parameter => parameter.ParameterType == typeof(HttpClient))));

        Assert.Equal(covered.OrderBy(type => type.Name), declared.OrderBy(type => type.Name));
    }

    private static ProviderCatalogueRequest TagPage()
        => new(WhisparrEntityKind.Tag, "70", 1, 40, null, null, new Dictionary<string, string>());

    private static CoveConfiguration Configured(string endpoint)
    {
        var config = new CoveConfiguration();
        config.Scraping.MetadataServers.Add(
            new MetadataServerInstance
            {
                Endpoint = endpoint,
                ApiKey = SomeKey,
                Name = endpoint,

                // Zero paces nothing, so these cases do not wait on a limiter to settle a question
                // about an answer.
                MaxRequestsPerMinute = 0,
            });

        return config;
    }
}
