using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Identity;

// Which endpoint spelling an identity row is written under.
//
// The host's merge writes its identity row under the spelling the host is configured with and
// dedupes those rows by exact string, while resolving an endpoint to a source on the registrable
// domain. A stamp under a different spelling of the same source gains a second row on the next
// merge, and no database constraint prevents it. So the configured spelling wins where the host
// has one for the source; where it has none, the standard address below is written.
//
// A row already written under one spelling stays under it. Changing the answer later is a data fix
// in the user's library, not a code change.
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
