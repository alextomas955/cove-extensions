using System.Net;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Providers;

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

    // No page rather than an empty one. The surface reads an empty page as a catalogue that listed
    // nothing.
    [Theory]
    [MemberData(nameof(ShippedCatalogues))]
    public async Task NoShippedCatalogueAnswersAPageWhereTheProviderRefused(string provider)
    {
        var catalogue = Shipped[provider](
            BodyRecordingHandler.Answering(HttpStatusCode.Unauthorized, "{}"));

        var answer = await catalogue.ReadPageAsync(TagPage(), TestCt);

        Assert.Null(answer.Page);
    }

    // A catalogue is a type that reads over an HttpClient of its own. The selector in front of them
    // holds none, which is how the filter below excludes it.
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

                // Zero paces nothing, so no case waits on the limiter.
                MaxRequestsPerMinute = 0,
            });

        return config;
    }
}
