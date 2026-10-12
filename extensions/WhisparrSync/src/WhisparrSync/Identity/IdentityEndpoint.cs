using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Identity;

// Which endpoint spelling an identity row is written under. The host dedupes those rows by exact
// string while resolving an endpoint to a source on the registrable domain, so a stamp under a
// second spelling of one source gains a row on the next merge and no constraint prevents it. The
// configured spelling therefore wins where the host has one; otherwise the standard address below.
// A row already written stays where it is: changing the answer later is a data fix, not a code
// change.
internal static class IdentityEndpoint
{
    internal const string StashDb = "https://stashdb.org/graphql";

    internal const string ThePornDb = "https://theporndb.net/graphql";

    internal static string Resolve(
        WhisparrGeneration generation,
        MetadataProviderEndpoints preferred,
        IReadOnlyList<string> configured)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(configured);

        var wanted = PreferredFor(generation, preferred);
        return configured.FirstOrDefault(spelling => EndpointMatchGuard.SameSource(spelling, wanted))
            ?? wanted;
    }

    internal static string PreferredFor(
        WhisparrGeneration generation, MetadataProviderEndpoints preferred)
    {
        ArgumentNullException.ThrowIfNull(preferred);

        var chosen = generation switch
        {
            WhisparrGeneration.V3 => preferred.V3,
            _ => preferred.V2,
        };

        return string.IsNullOrWhiteSpace(chosen) ? StandardFor(generation) : chosen.Trim();
    }

    private static string StandardFor(WhisparrGeneration generation)
        => generation == WhisparrGeneration.V3 ? StashDb : ThePornDb;
}
