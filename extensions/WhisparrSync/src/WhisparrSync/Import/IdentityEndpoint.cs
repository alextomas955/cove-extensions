using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Import;

/// <summary>
/// Which endpoint spelling an identity row is written under.
/// </summary>
/// <remarks>
/// Pure. The host's own merge writes its identity row under the spelling the host is CONFIGURED
/// with and dedupes those rows by exact string, while resolving an endpoint to a source on the
/// registrable domain. A stamp under a different spelling of the same source therefore gains a
/// second row on the next merge, and no database constraint prevents it.
/// <para>
/// So the configured spelling wins where the host has one for the source. Where it has none - the
/// host is configured with no source by default - the standard address below is written instead,
/// and a host configured at a non-standard address the option was not filled in for gets the
/// same answer.
/// </para>
/// <para>
/// A row already written under one spelling stays under it. Changing the answer later is a data fix
/// in the user's own library rather than a code change.
/// </para>
/// </remarks>
internal static class IdentityEndpoint
{
    /// <summary>The standard address of the source the v3 generation identifies against.</summary>
    internal const string StashDb = "https://stashdb.org/graphql";

    /// <summary>The standard address of the source the v2 generation identifies against.</summary>
    internal const string ThePornDb = "https://theporndb.net/graphql";

    /// <summary>
    /// The spelling to stamp for <paramref name="generation"/>: the configured source on the same
    /// registrable domain as the preferred address, or that address when none is configured there.
    /// </summary>
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

    /// <summary>
    /// The address <paramref name="generation"/> identifies against: the option's value, or the
    /// standard address when its slot is blank.
    /// </summary>
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
