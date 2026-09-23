using System.Text.RegularExpressions;

using Renamer.Options;

namespace Renamer.Planner;

// The per-batch routing lookups, built once per batch so the DestinationResolver never re-parses per
// entity. The studio, tag and exclude sets key on stable ids, never on names. The exact path maps use
// DestinationResolver.SourcePathComparer over NormalizeSourcePath keys. Every regex is parsed at build
// time with a match timeout that bounds a catastrophic-backtracking pattern, so an invalid pattern never
// reaches the resolver. A null or empty exclude member means none is configured.
//
// A studio exclude matches the entity's own StudioId or any of its ParentStudios ancestor ids.
public sealed record RouteLookups(
    IReadOnlyDictionary<int, Destination> StudioIdToDest,
    IReadOnlyDictionary<int, Destination> TagIdToDest,
    IReadOnlyDictionary<string, Destination> PathExactToDest,
    IReadOnlyList<(Regex Pattern, Destination Dest)> PathRegexRules,
    IReadOnlySet<int>? ExcludeTagIds = null,
    IReadOnlySet<int>? ExcludeStudioIds = null,
    IReadOnlySet<string>? ExcludePathsExact = null,
    IReadOnlyList<Regex>? ExcludePathRegex = null)
{
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);

    // A pattern that fails to parse is skipped and reported through onInvalidRegex, not the batch.
    public static RouteLookups From(RenamerOptions o, Action<string, string> onInvalidRegex)
    {
        var (exact, regexRules) = Split(o.PathDestinations.Select(r => (r.Pattern, r.IsRegex, r.Dest)), onInvalidRegex);
        var (excludeExact, excludeRegex) = Split(o.ExcludePaths.Select(r => (r.Pattern, r.IsRegex, true)), onInvalidRegex);

        return new RouteLookups(
            o.StudioDestinations,
            o.TagDestinations,
            exact,
            regexRules,
            new HashSet<int>(o.ExcludeTagIds),
            new HashSet<int>(o.ExcludeStudioIds),
            excludeExact.Keys.ToHashSet(DestinationResolver.SourcePathComparer),
            [.. excludeRegex.Select(r => r.Pattern)]);
    }

    // The first exact rule for a path wins, preserving user order.
    private static (Dictionary<string, T> Exact, List<(Regex Pattern, T Value)> Regexes) Split<T>(
        IEnumerable<(string Pattern, bool IsRegex, T Value)> rules, Action<string, string> onInvalidRegex)
    {
        var exact = new Dictionary<string, T>(DestinationResolver.SourcePathComparer);
        var regexes = new List<(Regex, T)>();
        foreach (var (pattern, isRegex, value) in rules)
        {
            if (!isRegex)
            {
                exact.TryAdd(DestinationResolver.NormalizeSourcePath(pattern), value);
                continue;
            }

            try
            {
                regexes.Add((new Regex(pattern, RegexOptions.None, RegexMatchTimeout), value));
            }
            catch (ArgumentException ex)
            {
                onInvalidRegex(pattern, ex.Message);
            }
        }

        return (exact, regexes);
    }
}
