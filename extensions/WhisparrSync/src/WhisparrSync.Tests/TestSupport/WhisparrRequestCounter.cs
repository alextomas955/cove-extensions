namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Classifies the outbound requests <see cref="FakeHttpMessageHandler"/> already captured, so a test can
/// assert how many times a code path reads Whisparr's ENTIRE movie set rather than only that it reached
/// the wire.
/// </summary>
/// <remarks>
/// <para>
/// The count is the number a read-shape change is supposed to move. Asserting it pins today's cost to a
/// stated formula, so a later narrowing changes a number some test already claims rather than an
/// unclaimed one — and so a "narrowing" that lowers peak memory while issuing MORE requests is visible
/// as the regression it is.
/// </para>
/// <para>
/// A request counts as a whole-set read only when it is a GET of the movie index carrying no value for
/// any parameter Whisparr binds as a filter, so a narrow read can never be tallied as a whole-set one.
/// An EMPTY filter value counts as whole-set, which is not a technicality: Whisparr's filter chain treats
/// an empty value as no filter and answers with the entire set.
/// </para>
/// </remarks>
internal static class WhisparrRequestCounter
{
    private const string MovieIndexPath = "/api/v3/movie";

    // The by-id hydration route. It is a POST carrying an id list and it answers movie rows — verified live
    // against 3.3.4.794 by reading the movie set before and after (8 rows, unchanged), not inferred from its
    // 200. Counting it as a write would make "this path issued no adds" unreadable, which is the one assertion
    // the unknown-entity gate turns on.
    private const string MovieBulkReadPath = "/api/v3/movie/bulk";

    // The three the movie index binds; every other query parameter (excludeLocalCovers, or one Whisparr
    // does not bind at all) leaves the read whole-set.
    private static readonly string[] NarrowingParameters = ["tmdbId", "tpdbId", "stashId"];

    /// <summary>The whole-movie-set read count for everything <paramref name="handler"/> has seen.</summary>
    internal static int WholeSetMovieReads(FakeHttpMessageHandler handler) => Classify(handler).WholeSetMovieReads;

    /// <summary>
    /// The full breakdown rather than a single total, so a failing assertion says which kind of call moved
    /// instead of only that the sum changed.
    /// </summary>
    internal static WhisparrRequestBreakdown Classify(FakeHttpMessageHandler handler)
    {
        int wholeSet = 0, narrow = 0, otherReads = 0, writes = 0;
        foreach (var request in handler.Requests)
        {
            if (IsPath(request.Url, MovieBulkReadPath))
            {
                narrow++;
            }
            else if (request.Method != HttpMethod.Get)
            {
                writes++;
            }
            else if (!IsMovieIndex(request.Url))
            {
                otherReads++;
            }
            else if (CarriesNarrowingValue(request.Url))
            {
                narrow++;
            }
            else
            {
                wholeSet++;
            }
        }

        return new WhisparrRequestBreakdown(wholeSet, narrow, otherReads, writes, handler.Requests.Count);
    }

    // The index only — a sub-route such as /api/v3/movie/lookup reads no set at all and must not be tallied.
    private static bool IsMovieIndex(string url) => IsPath(url, MovieIndexPath);

    private static bool IsPath(string url, string path)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && string.Equals(uri.AbsolutePath.TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase);

    private static bool CarriesNarrowingValue(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == pair.Length - 1)
            {
                continue;
            }

            var name = pair[..separator];
            if (NarrowingParameters.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>One run's outbound calls, split by what each one costs the instance.</summary>
/// <param name="WholeSetMovieReads">GETs of the movie index carrying no filter value — each pulls every row.</param>
/// <param name="NarrowMovieReads">
/// GETs of the movie index carrying a filter value, plus the by-id bulk hydration POST — each pulls only the
/// rows it named.
/// </param>
/// <param name="OtherReads">Every other GET (roots, tags, quality profiles, releases, per-entity reads).</param>
/// <param name="Writes">Every non-GET — an add, a monitor flip, a command.</param>
/// <param name="Total">Every captured request, so the three read buckets plus the writes can be reconciled.</param>
internal sealed record WhisparrRequestBreakdown(
    int WholeSetMovieReads, int NarrowMovieReads, int OtherReads, int Writes, int Total);
