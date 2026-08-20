using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using static global::Renamer.Execution.PathOps;

namespace Renamer.Options;

/// <summary>
/// The one-time conversions of a stored options blob into the shape <see cref="RenamerOptions"/> now
/// declares — NAME-keyed rules to id-keyed ones, and a typed destination ROOT to a Cove library path
/// plus a relative template — plus the schema stamp that keeps them one-time.
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

    /// <summary>
    /// The stamp value written once the stored blob is id-keyed AND its destination rules name a Cove
    /// library path plus a relative template.
    /// </summary>
    /// <remarks>
    /// <c>"3"</c> adds the destination conversion to the <c>"2"</c> id-keying. The two ride one stamp
    /// because a blob is either current or it is not, and a second stamp would be a second thing to keep
    /// in step; the seam runs whichever halves the blob still needs, each recognizing its own input.
    /// <para>
    /// RETIREMENT CONDITION for the destination half, written here because this is the one moment
    /// anyone knows what it is: it can be deleted once no installed store can still hold a schema-2
    /// blob — that is, once the release carrying this conversion has been out long enough that an
    /// upgrade path skipping it is unsupported. There is no date, and inventing one would be worse than
    /// the condition; what is knowable is the test, which is that <c>ConvertDestinationsToRoots</c> can
    /// find no site (every stored destination is already an object rather than a string). Deleting it
    /// early costs a user their whole routing configuration silently, since an unconverted string value
    /// deserializes to nothing.
    /// </para>
    /// <para>
    /// The name→id half has its OWN condition and reaches it earlier: it goes once no store can still
    /// hold a name-keyed blob, which is a strictly older release than the one this stamp adds. One stamp
    /// covering both is deliberate and does not make them retire together — read each half's condition
    /// before deleting either, and expect to delete them in separate releases.
    /// </para>
    /// </remarks>
    public const string CurrentSchema = "3";

    private const string PerformersGroup = "Performers";
    private const string TagsGroup = "Tags";
    private const string LegacyWhitelist = "Whitelist";
    private const string LegacyBlacklist = "Blacklist";
    private const string WhitelistIds = "WhitelistIds";
    private const string BlacklistIds = "BlacklistIds";
    private const string LegacyExcludeTags = "ExcludeTags";
    private const string ExcludeTagIds = "ExcludeTagIds";
    private const string TagDestinations = "TagDestinations";
    private const string StudioDestinations = "StudioDestinations";
    private const string PathDestinations = "PathDestinations";
    private const string UnorganizedDestination = "UnorganizedDestination";
    private const string FolderTemplate = "FolderTemplate";
    private const string PathRuleDest = "Dest";

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

    /// <summary>
    /// A destination rule whose key resolved to an id an earlier key had already routed, and which is
    /// therefore gone: the surviving destination is <paramref name="ClaimedBy"/>'s.
    /// </summary>
    public sealed record DiscardedDestination(string Key, int Id, string ClaimedBy);

    /// <summary>The converted blob and everything the conversion discarded or narrowed on the way.</summary>
    public sealed record Conversion(
        string Json,
        IReadOnlyList<string> DroppedNames,
        IReadOnlyList<CaseCollapse> CaseCollapses,
        IReadOnlyList<DiscardedDestination> DiscardedDestinations);

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
            return new Conversion(json, [], [], []);
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

        return new Conversion(
            root.ToJsonString(), trail.Dropped, trail.Collapsed, trail.DiscardedDestinations);
    }

    /// <summary>One destination rule rewritten from a typed root into a library root + relative template.</summary>
    /// <param name="Rule">Which rule, for the log: the field and its key or index.</param>
    /// <param name="From">The value as stored — a typed absolute destination root.</param>
    /// <param name="ToRoot">The Cove library path the stored root turned out to live under.</param>
    /// <param name="ToTemplate">The relative template written under it — the remainder plus the old global folder template.</param>
    public sealed record RewrittenDestination(string Rule, string From, string ToRoot, string ToTemplate);

    /// <summary>One destination rule removed because its stored root lies under no Cove library path.</summary>
    /// <param name="Rule">Which rule, for the log: the field and its key or index.</param>
    /// <param name="Stored">The stored root that could not be placed.</param>
    public sealed record DroppedDestination(string Rule, string Stored);

    /// <summary>The destination conversion's result: the blob, what it rewrote, and what it removed.</summary>
    /// <param name="Json">The converted blob, or the input unchanged when nothing was done.</param>
    /// <param name="Rewritten">Every rule whose stored root was placed under a library path.</param>
    /// <param name="Dropped">Every rule removed for lying under no library path.</param>
    /// <param name="Deferred">
    /// True when there was work to do and no library paths to do it against, so NOTHING was changed and
    /// the caller must not stamp. Distinguished from "no work" because the two look identical in the
    /// blob and differ completely in consequence: converting against an empty list would drop every
    /// rule the user has.
    /// </param>
    /// <param name="RemovedEmptyRoutes">
    /// How many stored destinations were REMOVED rather than rewritten, because the field spells "there
    /// is no route" as the absent member and the blob still holds the empty value that used to spell it.
    /// Counted rather than listed, and deliberately not logged: nothing about the user's routing changes,
    /// so there is nothing to tell them — but the blob did change, and a caller that reads only
    /// <see cref="Rewritten"/> and <see cref="Dropped"/> would leave a value the current model cannot
    /// bind sitting in the store.
    /// </param>
    public sealed record DestinationConversion(
        string Json,
        IReadOnlyList<RewrittenDestination> Rewritten,
        IReadOnlyList<DroppedDestination> Dropped,
        bool Deferred,
        int RemovedEmptyRoutes = 0)
    {
        /// <summary>True iff the blob was altered, so it is worth writing back.</summary>
        public bool Changed => Rewritten.Count > 0 || Dropped.Count > 0 || RemovedEmptyRoutes > 0;
    }

    /// <summary>
    /// Rewrites every destination rule in <paramref name="json"/> from a typed absolute ROOT into the
    /// one shape a destination now has: a root CHOSEN from <paramref name="libraryRoots"/>, plus the
    /// relative template rendered under it.
    /// </summary>
    /// <remarks>
    /// Behaviour-preserving, and mechanically so rather than by judgement: a matched rule used to land
    /// an item at <c>root</c> with the global folder template rendered underneath, so the same
    /// destination is <c>(the library path containing root)</c> plus <c>(the rest of root)/(that same
    /// template)</c>. A rule storing <c>I:/Downloads/P/videos</c> under library path
    /// <c>I:/Downloads/P</c> with a <c>$studio</c> template becomes root <c>I:/Downloads/P</c>,
    /// template <c>videos/$studio</c> — the identical folder, so nothing moves on the first run after
    /// the conversion.
    /// <para>
    /// A stored root under NO library path is DROPPED, by owner decision: there is no root to choose
    /// for it, and inventing one would relocate files. Its items follow the default destination
    /// afterwards, which is a behaviour change and is why every drop is logged and the CHANGELOG says
    /// so.
    /// </para>
    /// <para>
    /// The global folder template is left exactly as stored, and no root is written beside it. That is
    /// the whole of the default's conversion: <c>FolderRoot</c>'s own default is "the file's own
    /// library path", which is what a relative template has always been measured from, so the absent
    /// key already means the right thing and writing one would only be a chance to write it wrongly.
    /// </para>
    /// </remarks>
    /// <param name="json">The stored blob, id-keyed (run after <see cref="Convert"/>).</param>
    /// <param name="libraryRoots">Cove's configured library paths, the only roots a destination may choose.</param>
    public static DestinationConversion ConvertDestinationsToRoots(
        string json, IReadOnlyList<string> libraryRoots)
    {
        var root = TryParse(json);
        if (root is null)
        {
            return new DestinationConversion(json, [], [], Deferred: false);
        }

        string folderTemplate = ReadString(root, FolderTemplate) ?? string.Empty;
        var sites = CollectDestinationSites(root);
        if (sites.Count == 0)
        {
            return new DestinationConversion(json, [], [], Deferred: false);
        }

        // Nothing to choose a root FROM. Refusing here is the same safety argument the name-to-id half
        // makes about an empty library read: an empty list is indistinguishable from "the host has not
        // told us yet", and converting against it would drop every rule the user has.
        if (libraryRoots.Count == 0)
        {
            return new DestinationConversion(json, [], [], Deferred: true);
        }

        var rewritten = new List<RewrittenDestination>();
        var dropped = new List<DroppedDestination>();
        int removedEmptyRoutes = 0;

        foreach (var site in sites)
        {
            string stored = site.Stored;

            if (stored.Length == 0)
            {
                // Two different questions share the empty string, and only the site knows which one it
                // is answering — see DestinationSite.EmptyIsNoRoute.
                if (site.EmptyIsNoRoute)
                {
                    site.Drop();
                    removedEmptyRoutes++;
                    continue;
                }

                // A rule storing no root at all named no destination of its own; it rendered the global
                // folder template under the file's own place. "The file's own library path" is that same
                // arrangement, so it converts without needing a root and cannot be dropped.
                site.Write(string.Empty, folderTemplate);
                rewritten.Add(new RewrittenDestination(site.Rule, stored, string.Empty, folderTemplate));
                continue;
            }

            string? containing = Planner.PathConfinement.ContainingRoot(stored, libraryRoots);
            if (containing is null)
            {
                site.Drop();
                dropped.Add(new DroppedDestination(site.Rule, stored));
                continue;
            }

            string remainder = NormalizeSlash(stored).TrimEnd('/')[containing.Length..].Trim('/');
            string template = JoinPath(remainder, folderTemplate);
            site.Write(containing, template);
            rewritten.Add(new RewrittenDestination(site.Rule, stored, containing, template));
        }

        return new DestinationConversion(
            root.ToJsonString(), rewritten, dropped, Deferred: false, removedEmptyRoutes);
    }

    /// <summary>
    /// One stored destination the conversion has to rewrite: its label for the log, the root as stored,
    /// and the two edits — replace with a root/template pair, or remove the rule entirely.
    /// </summary>
    /// <remarks>
    /// A site rather than four near-identical loops, because the four fields differ only in how a value
    /// is reached (a map entry, an array element's member, a top-level member) while the DECISION about
    /// it is one rule. Splitting them would be four places for that rule to drift.
    /// <para>
    /// <c>EmptyIsNoRoute</c> is true for the one field where an empty stored value means "there is no
    /// route at all" rather than "this rule names no root of its own". The current model spells the first
    /// as the ABSENT member, so such a site is REMOVED rather than converted: a bare JSON string cannot
    /// bind to <see cref="Destination"/>, and leaving one in place makes the WHOLE stored blob
    /// unreadable — the options store answers a failed bind with defaults, so every setting the user
    /// configured silently reads as unset. Converting it instead would be the mirror failure, turning
    /// "unorganized items are not routed" into a route.
    /// </para>
    /// </remarks>
    private sealed record DestinationSite(
        string Rule,
        string Stored,
        Action<string, string> Write,
        Action Drop,
        bool EmptyIsNoRoute = false);

    /// <summary>Every stored destination in the blob, skipping anything that is not a JSON string.</summary>
    private static List<DestinationSite> CollectDestinationSites(JsonObject root)
    {
        var sites = new List<DestinationSite>();

        foreach (string mapName in new[] { StudioDestinations, TagDestinations })
        {
            if (Find(root, mapName)?.Value is not JsonObject map)
            {
                continue;
            }

            foreach (string key in map.Select(entry => entry.Key).ToList())
            {
                if (map[key] is not JsonValue value || !value.TryGetValue(out string? stored))
                {
                    continue;
                }

                string mapKey = key;
                sites.Add(new DestinationSite(
                    $"{mapName}[{mapKey}]",
                    stored,
                    (r, t) => map[mapKey] = Pair(r, t),
                    () => map.Remove(mapKey)));
            }
        }

        if (Find(root, PathDestinations)?.Value is JsonArray rules)
        {
            // Walked in REVERSE so a drop removing an element never shifts an index a later site closed
            // over — the one ordering fact in here that a reader cannot get from the loop shape.
            for (int i = rules.Count - 1; i >= 0; i--)
            {
                if (rules[i] is not JsonObject rule
                    || Find(rule, PathRuleDest) is not { Value: JsonValue destValue } destEntry
                    || !destValue.TryGetValue(out string? stored))
                {
                    continue;
                }

                int index = i;
                string member = destEntry.Key;
                sites.Add(new DestinationSite(
                    $"{PathDestinations}[{index}]",
                    stored,
                    (r, t) => rule[member] = Pair(r, t),
                    () => rules.RemoveAt(index)));
            }
        }

        if (Find(root, UnorganizedDestination) is { Value: JsonValue unorganizedValue } unorganizedEntry
            && unorganizedValue.TryGetValue(out string? unorganized))
        {
            string member = unorganizedEntry.Key;
            sites.Add(new DestinationSite(
                UnorganizedDestination,
                unorganized,
                (r, t) => root[member] = Pair(r, t),
                () => root.Remove(member),
                EmptyIsNoRoute: true));
        }

        return sites;
    }

    /// <summary>The stored form of a destination: the pair the current model deserializes.</summary>
    private static JsonObject Pair(string root, string template) => new()
    {
        ["Root"] = JsonValue.Create(root),
        ["Template"] = JsonValue.Create(template),
    };

    /// <summary>Reads a string-valued member by name (case-insensitively), or null when absent or not a string.</summary>
    private static string? ReadString(JsonObject owner, string name)
        => Find(owner, name)?.Value is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    /// <summary>Everything the conversion discarded or narrowed, accumulated across every field.</summary>
    private sealed class Trail
    {
        public List<string> Dropped { get; } = [];

        public List<CaseCollapse> Collapsed { get; } = [];

        public List<DiscardedDestination> DiscardedDestinations { get; } = [];

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

        // Which stored key claimed each id, so a rule that loses can name the one that beat it. Two keys
        // reach the same id both by case variance ("4K" and "4k" against one tag) and on a half-converted
        // map where an id key and a name key meet — and either way the loser used to vanish with no
        // trace at all, its destination decided by nothing but JSON document order.
        var claimedBy = new Dictionary<int, string>();

        foreach (var (key, value) in legacy)
        {
            int id;
            if (IsIdKey(key))
            {
                id = int.Parse(key, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            else if (!TryResolve(lookup, key, trail, out id))
            {
                continue;
            }

            if (claimedBy.TryGetValue(id, out string? winner))
            {
                trail.DiscardedDestinations.Add(new DiscardedDestination(key, id, winner));
                continue;
            }

            claimedBy[id] = key;
            converted[id.ToString(CultureInfo.InvariantCulture)] = value?.DeepClone();
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
