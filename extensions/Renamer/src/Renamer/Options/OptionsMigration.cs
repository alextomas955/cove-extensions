using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Renamer.Options;

/// <summary>
/// The one-time conversion of a stored options blob from the NAME-keyed shape shipped before the
/// identity migration to the id-keyed shape <see cref="RenamerOptions"/> now declares, plus the schema
/// stamp that keeps it one-time.
/// </summary>
/// <remarks>
/// Works on the raw JSON rather than on <see cref="RenamerOptions"/>, for two reasons a typed converter
/// cannot meet. A legacy blob does not bind to the current model at all — a name-keyed
/// <c>TagDestinations</c> makes <c>JsonSerializer</c> throw, and the options store answers a throw by
/// returning DEFAULTS, so a typed converter would convert defaults and then persist them over the
/// user's settings. And a stored key this converter does not model is carried through verbatim, so a
/// hand-edited or newer-version key survives a conversion that does not understand it.
/// <para>
/// Pure: no store, no database context, no clock, no host type. The read, the zero-row refusal and the
/// write live at the initialize-time seam that calls this.
/// </para>
/// </remarks>
public static class OptionsMigration
{
    /// <summary>The store key holding the options-schema stamp that makes the conversion one-time.</summary>
    public const string SchemaKey = "options.schema";

    /// <summary>The stamp value written once the stored blob is id-keyed.</summary>
    public const string CurrentSchema = "2";

    private const string PerformersGroup = "Performers";
    private const string TagsGroup = "Tags";
    private const string LegacyWhitelist = "Whitelist";
    private const string LegacyBlacklist = "Blacklist";
    private const string WhitelistIds = "WhitelistIds";
    private const string BlacklistIds = "BlacklistIds";
    private const string LegacyExcludeTags = "ExcludeTags";
    private const string ExcludeTagIds = "ExcludeTagIds";
    private const string TagDestinations = "TagDestinations";

    /// <summary>
    /// How many stored names in each half still have to be resolved against a live entity table.
    /// </summary>
    /// <remarks>
    /// Counted, not flagged, because a legacy key being PRESENT says nothing about there being anything
    /// to resolve: the pre-migration panel serialized its whole defaults object, so an install that
    /// never touched either group still stored an empty <c>Whitelist</c>, <c>Blacklist</c> and
    /// <c>ExcludeTags</c>. Treating those as work would demand rows from a table the user legitimately
    /// has none of, and the conversion would defer on every start forever.
    /// </remarks>
    public readonly record struct LegacyNames(int Tags, int Performers)
    {
        /// <summary>True when anything at all still needs converting.</summary>
        public bool Any => Tags > 0 || Performers > 0;
    }

    /// <summary>
    /// A stored rule name that matched several entities differing only by letter case, and so now
    /// applies to one of them where before the migration it applied to all.
    /// </summary>
    public sealed record CaseCollapse(string Name, int MatchedId, IReadOnlyList<int> AlsoMatchedIds);

    /// <summary>The converted blob and everything the conversion discarded or narrowed on the way.</summary>
    public sealed record Conversion(
        string Json,
        IReadOnlyList<string> DroppedNames,
        IReadOnlyList<CaseCollapse> CaseCollapses);

    /// <summary>
    /// Counts the names each half of <paramref name="json"/> still needs resolved, without touching a
    /// database. An absent, blank or unparseable blob needs nothing: there is no configuration to lose.
    /// </summary>
    public static LegacyNames Scan(string? json)
    {
        var root = TryParse(json);
        if (root is null)
        {
            return default;
        }

        int tags = CountNames(root, LegacyExcludeTags)
            + CountGroupNames(root, TagsGroup)
            + CountNameKeyedDestinations(root);

        return new LegacyNames(tags, CountGroupNames(root, PerformersGroup));
    }

    /// <summary>
    /// Resolves every name-keyed rule in <paramref name="json"/> against <paramref name="tags"/> and
    /// <paramref name="performers"/>, returning the converted blob and the names that resolved to
    /// nothing. Every other stored key is carried through unchanged.
    /// </summary>
    /// <param name="json">The stored blob, in whatever shape it currently holds.</param>
    /// <param name="tags">Every tag row's id paired with its name.</param>
    /// <param name="performers">Every performer row's id paired with its name.</param>
    /// <returns>
    /// The conversion, or the input unchanged with no dropped names when the blob does not parse.
    /// </returns>
    public static Conversion Convert(
        string json,
        IReadOnlyList<(int Id, string Name)> tags,
        IReadOnlyList<(int Id, string Name)> performers)
    {
        var root = TryParse(json);
        if (root is null)
        {
            return new Conversion(json, [], []);
        }

        var tagIds = BuildLookup(tags);
        var performerIds = BuildLookup(performers);
        var trail = new Trail();

        var tagGroup = Find(root, TagsGroup)?.Value as JsonObject;
        var performerGroup = Find(root, PerformersGroup)?.Value as JsonObject;

        ConvertNameList(tagGroup, LegacyWhitelist, WhitelistIds, tagIds, trail);
        ConvertNameList(tagGroup, LegacyBlacklist, BlacklistIds, tagIds, trail);
        ConvertNameList(performerGroup, LegacyWhitelist, WhitelistIds, performerIds, trail);
        ConvertNameList(performerGroup, LegacyBlacklist, BlacklistIds, performerIds, trail);
        ConvertNameList(root, LegacyExcludeTags, ExcludeTagIds, tagIds, trail);
        ConvertDestinations(root, tagIds, trail);

        return new Conversion(root.ToJsonString(), trail.Dropped, trail.Collapsed);
    }

    /// <summary>Everything the conversion discarded or narrowed, accumulated across every field.</summary>
    private sealed class Trail
    {
        public List<string> Dropped { get; } = [];

        public List<CaseCollapse> Collapsed { get; } = [];

        /// <summary>
        /// Records a narrowing once per stored name rather than once per field that carries it: the
        /// same rule name in a whitelist and an exclude list narrows for one reason, and reporting it
        /// twice reads as two separate library problems.
        /// </summary>
        public void RecordCollapse(string name, int matchedId, IReadOnlyList<int> alsoMatchedIds)
        {
            if (!Collapsed.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Collapsed.Add(new CaseCollapse(name, matchedId, alsoMatchedIds));
            }
        }
    }

    private static JsonObject? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Finds a member by name, ignoring letter case, and reports the key as actually spelled.</summary>
    /// <remarks>
    /// The blob is parsed with the DEFAULT ordinal key comparer, and the case-insensitivity that
    /// <see cref="RenamerOptions.JsonOptions"/> reads with is applied here instead. Parsing
    /// case-insensitively would fold the user's own map keys too, and a destination map holding both
    /// <c>4K</c> and <c>4k</c> — which the panel's string-keyed editor can write — then throws
    /// <see cref="ArgumentException"/> the first time it is enumerated, taking the whole conversion with it.
    /// </remarks>
    private static (string Key, JsonNode? Value)? Find(JsonObject owner, string name)
    {
        foreach (var (key, value) in owner)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return (key, value);
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the name → ids lookup used by every field, ascending, so the first id is the match and any
    /// others are what the match no longer covers.
    /// </summary>
    /// <remarks>
    /// Resolution is case-insensitive because matching a stored rule against a live entity name was
    /// case-insensitive before this migration: a rule stored as <c>"4k"</c> against a tag named
    /// <c>4K</c> was live, and a case-sensitive lookup would silently drop it.
    /// <para>
    /// Where several entities differ only by letter case they share one entry, and a stored rule that
    /// matched ALL of them now matches the first — the one genuine narrowing this migration carries, and
    /// why every id is kept rather than only the winner: without the rest, the narrowing leaves no trace
    /// at all (the name resolved, so nothing is dropped). The lowest id wins so the choice is decided by
    /// the data rather than by the order the rows came back in.
    /// </para>
    /// </remarks>
    private static Dictionary<string, int[]> BuildLookup(IReadOnlyList<(int Id, string Name)> rows)
    {
        var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in rows)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!byName.TryGetValue(name, out var ids))
            {
                byName[name] = ids = [];
            }

            ids.Add(id);
        }

        return byName.ToDictionary(
            entry => entry.Key, entry => (int[])[.. entry.Value.Order()], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves one stored name, recording the narrowing when the name covered more than one entity.
    /// </summary>
    /// <remarks>
    /// Only a name a stored rule actually carries is reported: a pair of case-variant rows no rule names
    /// changes nothing for the user, and listing every such pair in a large library would bury the ones
    /// that did.
    /// </remarks>
    private static bool TryResolve(Dictionary<string, int[]> lookup, string name, Trail trail, out int id)
    {
        if (!lookup.TryGetValue(name, out int[]? ids))
        {
            trail.Dropped.Add(name);
            id = 0;
            return false;
        }

        id = ids[0];
        if (ids.Length > 1)
        {
            trail.RecordCollapse(name, id, ids[1..]);
        }

        return true;
    }

    private static void ConvertNameList(
        JsonObject? owner,
        string legacyName,
        string idName,
        Dictionary<string, int[]> lookup,
        Trail trail)
    {
        if (owner is null || Find(owner, legacyName) is not { Value: JsonArray legacy } legacyEntry)
        {
            return;
        }

        var ids = new List<int>();
        var seen = new HashSet<int>();

        // A half-converted blob carrying both spellings keeps the ids it already had.
        var idEntry = Find(owner, idName);
        if (idEntry?.Value is JsonArray existing)
        {
            foreach (var node in existing)
            {
                if (node is JsonValue value && value.TryGetValue(out int id) && seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        foreach (var node in legacy)
        {
            if (node is not JsonValue value || !value.TryGetValue(out string? name) || name is null)
            {
                continue;
            }

            if (!TryResolve(lookup, name, trail, out int id))
            {
                continue;
            }

            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        owner[idEntry?.Key ?? idName] = new JsonArray([.. ids.Select(id => (JsonNode)JsonValue.Create(id))]);
        owner.Remove(legacyEntry.Key);
    }

    private static void ConvertDestinations(
        JsonObject root,
        Dictionary<string, int[]> lookup,
        Trail trail)
    {
        if (Find(root, TagDestinations) is not { Value: JsonObject legacy } entry)
        {
            return;
        }

        var converted = new JsonObject();
        foreach (var (key, value) in legacy)
        {
            string? idKey = IsIdKey(key)
                ? key
                : TryResolve(lookup, key, trail, out int id)
                    ? id.ToString(CultureInfo.InvariantCulture)
                    : null;

            if (idKey is null || converted.ContainsKey(idKey))
            {
                continue;
            }

            converted[idKey] = value?.DeepClone();
        }

        root[entry.Key] = converted;
    }

    // Counted with the SAME string-valued predicate ConvertNameList resolves with, so the two can never
    // disagree about whether a stored entry is a name awaiting an id.
    private static int CountNames(JsonObject owner, string legacyName) =>
        Find(owner, legacyName)?.Value is JsonArray list
            ? list.Count(node => node is JsonValue value && value.TryGetValue(out string? _))
            : 0;

    private static int CountGroupNames(JsonObject root, string group) =>
        Find(root, group)?.Value is JsonObject g
            ? CountNames(g, LegacyWhitelist) + CountNames(g, LegacyBlacklist)
            : 0;

    private static int CountNameKeyedDestinations(JsonObject root) =>
        Find(root, TagDestinations)?.Value is JsonObject map
            ? map.Count(entry => !IsIdKey(entry.Key))
            : 0;

    // A converted map's keys are the invariant decimal spelling of an int, which is also what a
    // fresh install writes before this conversion ever runs — so an int-spelled key is read as an id
    // that is already migrated, never as a tag name. A tag whose name is a bare number therefore
    // cannot carry a destination rule through the conversion; treating it as a name instead would
    // destroy the routing of every install that already stores ids.
    private static bool IsIdKey(string key) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
        && id.ToString(CultureInfo.InvariantCulture) == key;
}
