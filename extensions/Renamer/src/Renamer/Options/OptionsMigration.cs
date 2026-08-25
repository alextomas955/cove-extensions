using System.Text.Json;
using System.Text.Json.Nodes;

using static global::Renamer.Execution.PathOps;

namespace Renamer.Options;

/// <summary>
/// The one-time conversions of a stored options blob into the shape <see cref="RenamerOptions"/> now
/// declares - NAME-keyed entity rules to id-keyed ones, and a typed destination ROOT to a Cove
/// library path plus a relative template - plus the schema stamp that keeps them one-time.
/// </summary>
/// <remarks>
/// Works on the raw JSON rather than on <see cref="RenamerOptions"/>, for two reasons a typed
/// converter cannot meet. A legacy blob does not bind to the current model at all - a name-keyed
/// <c>TagDestinations</c> makes <see cref="JsonSerializer"/> throw, and the options store answers a
/// throw by returning DEFAULTS, so a typed converter would convert defaults and then persist them
/// over the user's settings. And a stored key this converter does not model is carried through
/// verbatim, so a hand-edited or newer-version key survives a conversion that does not understand it.
/// <para>
/// Pure: no store, no database context, no clock, no host type. The read, the zero-row refusal and
/// the write live at the initialize-time seam that calls this.
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
    /// The two halves ride one stamp because a blob is either current or it is not, and a second stamp
    /// would be a second thing to keep in step; the seam runs whichever halves the blob still needs,
    /// each recognizing its own input.
    /// <para>
    /// RETIREMENT CONDITION for the name half, written here because this is the one moment anyone
    /// knows what it is: it can be deleted once no installed store can still hold a name-keyed blob,
    /// that is, once the release carrying it has been out long enough that an upgrade path skipping it
    /// is unsupported. There is no date, and inventing one would be worse than the condition; what is
    /// knowable is the test, which is that <see cref="Scan"/> can find no name to resolve on any real
    /// install. Deleting it early costs a user every entity rule they configured, silently, because an
    /// unconverted name-keyed blob fails to bind and the store answers that with defaults.
    /// </para>
    /// <para>
    /// The destination half has its OWN condition and reaches it later: it goes once no store can
    /// still hold a blob whose destinations are bare strings, which is a strictly newer release than
    /// the one the name half waits on. Read each condition before deleting either, and expect to
    /// delete them in separate releases. Its test is that
    /// <see cref="ConvertDestinationsToRoots"/> can find no site to rewrite. Deleting it early costs a
    /// user their whole routing configuration silently, since an unconverted string value binds to
    /// nothing.
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

    /// <summary>The distinct stored names each half still has to resolve against a live entity table.</summary>
    /// <remarks>
    /// The names themselves rather than a flag or a count, because they are also the query input: the
    /// seam resolves exactly these and nothing else, so the read is bounded by how many rules the user
    /// wrote instead of by how many tags the library holds.
    /// <para>
    /// A legacy key being PRESENT says nothing about there being anything to resolve: the pre-migration
    /// panel serialized its whole defaults object, so an install that never touched either group still
    /// stored an empty <c>Whitelist</c>, <c>Blacklist</c> and <c>ExcludeTags</c>. Treating those as work
    /// would demand rows from a table the user legitimately has none of, and the conversion would defer
    /// on every start forever.
    /// </para>
    /// </remarks>
    public readonly record struct LegacyNames(IReadOnlyList<string> Tags, IReadOnlyList<string> Performers)
    {
        /// <summary>True when anything at all still needs resolving.</summary>
        public bool Any => Tags.Count > 0 || Performers.Count > 0;
    }

    /// <summary>
    /// A stored rule name that matched several entities differing only by letter case, and so now
    /// applies to one of them where before the conversion it applied to all.
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
    /// The distinct names <paramref name="json"/> still needs resolved, without touching a database.
    /// An absent, blank or unparseable blob needs nothing: there is no configuration to lose.
    /// </summary>
    public static LegacyNames Scan(string? json)
    {
        var root = TryParse(json);
        if (root is null)
        {
            return new LegacyNames([], []);
        }

        var tags = new List<string>();
        Collect(tags, NamesIn(root, LegacyExcludeTags));
        Collect(tags, NamesIn(Group(root, TagsGroup), LegacyWhitelist));
        Collect(tags, NamesIn(Group(root, TagsGroup), LegacyBlacklist));
        Collect(tags, KeysIn(root, TagDestinations).Where(k => !IsId(k)));

        var performers = new List<string>();
        Collect(performers, NamesIn(Group(root, PerformersGroup), LegacyWhitelist));
        Collect(performers, NamesIn(Group(root, PerformersGroup), LegacyBlacklist));

        return new LegacyNames(Distinct(tags), Distinct(performers));
    }

    /// <summary>
    /// Rewrites every name-keyed entity rule in <paramref name="json"/> to the id it resolves to,
    /// against the rows the caller looked up.
    /// </summary>
    /// <remarks>
    /// The caller supplies ROWS rather than a name-to-id map so the case-collapse detection below is
    /// testable with no database: a map keyed case-insensitively has already collapsed the very
    /// duplicates that need reporting.
    /// <para>
    /// A name matching no row is DROPPED and reported. That is the only safe reading - a rule naming an
    /// entity that no longer exists cannot be honoured, and keeping the name would leave a rule that
    /// silently never matches.
    /// </para>
    /// </remarks>
    public static Conversion Convert(
        string? json,
        IReadOnlyList<(int Id, string Name)> tags,
        IReadOnlyList<(int Id, string Name)> performers)
    {
        var root = TryParse(json);
        if (root is null)
        {
            return new Conversion(json ?? string.Empty, [], [], []);
        }

        var dropped = new List<string>();
        var collapses = new List<CaseCollapse>();
        var discarded = new List<DiscardedDestination>();

        var tagLookup = BuildLookup(tags);
        var performerLookup = BuildLookup(performers);

        ConvertNameList(root, LegacyExcludeTags, ExcludeTagIds, tagLookup, dropped, collapses);
        ConvertGroup(root, TagsGroup, tagLookup, dropped, collapses);
        ConvertGroup(root, PerformersGroup, performerLookup, dropped, collapses);
        ConvertDestinationKeys(root, tagLookup, dropped, collapses, discarded);

        return new Conversion(root.ToJsonString(), dropped, collapses, discarded);
    }

    /// <summary>One destination rule rewritten from a typed root into a library root + relative template.</summary>
    /// <param name="Rule">Which rule, for the log: the field and its key or index.</param>
    /// <param name="From">The value as stored, a typed absolute destination root.</param>
    /// <param name="ToRoot">The Cove library path the stored root turned out to live under.</param>
    /// <param name="ToTemplate">The relative template written under it: the remainder plus the stored global folder template.</param>
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
    /// True when there was work to do and no library paths to do it against, so NOTHING was changed
    /// and the caller must not stamp. Distinguished from "no work" because the two look identical in
    /// the blob and differ completely in consequence: converting against an empty list would drop
    /// every rule the user has.
    /// </param>
    /// <param name="RemovedEmptyRoutes">
    /// How many stored destinations were REMOVED rather than rewritten, because the field spells
    /// "there is no route" as the absent member and the blob still holds the empty value that used to
    /// spell it. Counted rather than listed, and deliberately not logged: nothing about the user's
    /// routing changes. The blob did change, though, so a caller reading only <see cref="Rewritten"/>
    /// and <see cref="Dropped"/> would leave a value the current model cannot bind sitting in the
    /// store.
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
    /// one shape a destination now has: a root chosen from <paramref name="libraryRoots"/>, plus the
    /// relative template rendered under it.
    /// </summary>
    /// <remarks>
    /// Behaviour-preserving, and mechanically so rather than by judgement: a matched rule used to land
    /// an item at <c>root</c> with the global folder template rendered underneath, so the same
    /// destination is (the library path containing <c>root</c>) plus (the rest of <c>root</c>)/(that
    /// same template). A rule storing <c>I:/Downloads/P/videos</c> under library path
    /// <c>I:/Downloads/P</c> with a <c>$studio</c> template becomes root <c>I:/Downloads/P</c>,
    /// template <c>videos/$studio</c>: the identical folder, so nothing moves on the first run after
    /// the conversion.
    /// <para>
    /// A stored root under NO library path is DROPPED: there is no root to choose for it, and
    /// inventing one would relocate files. Its items follow the default destination afterwards, which
    /// is a behaviour change, which is why every drop is logged.
    /// </para>
    /// <para>
    /// The global folder template is left exactly as stored, and no root is written beside it. That is
    /// the whole of the default's conversion: <see cref="RenamerOptions.FolderRoot"/>'s own default is
    /// the file's own library path, which is what a relative template has always been measured from,
    /// so the absent key already means the right thing.
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

        string folderTemplate = StringOf(Property(root, FolderTemplate)) ?? string.Empty;
        var sites = CollectDestinationSites(root);
        if (sites.Count == 0)
        {
            return new DestinationConversion(json, [], [], Deferred: false);
        }

        // Nothing to choose a root FROM. Refusing here is the same safety argument the name half makes
        // about an empty entity table: an empty list is indistinguishable from "the host has not told
        // us yet", and converting against it would drop every rule the user has.
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
                // is answering - see DestinationSite.EmptyIsNoRoute.
                if (site.EmptyIsNoRoute)
                {
                    site.Drop();
                    removedEmptyRoutes++;
                    continue;
                }

                // A rule storing no root at all named no destination of its own; it rendered the global
                // folder template under the file's own place. The file's own library path is that same
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
    /// One stored destination the conversion has to rewrite: its label for the log, the root as
    /// stored, and the two edits - replace with a root/template pair, or remove the rule entirely.
    /// </summary>
    /// <remarks>
    /// A site rather than four near-identical loops, because the four fields differ only in how a
    /// value is reached (a map entry, an array element's member, a top-level member) while the
    /// DECISION about it is one rule.
    /// <para>
    /// <c>EmptyIsNoRoute</c> is true for the one field where an empty stored value means "there is no
    /// route at all" rather than "this rule names no root of its own". The current model spells the
    /// first as the ABSENT member, so such a site is REMOVED rather than converted: a bare JSON string
    /// cannot bind to <see cref="Destination"/>, and leaving one in place makes the WHOLE stored blob
    /// unreadable - the options store answers a failed bind with defaults, so every setting the user
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
            if (Property(root, mapName) is not JsonObject map)
            {
                continue;
            }

            foreach (string key in map.Select(entry => entry.Key).ToList())
            {
                if (StringOf(map[key]) is not { } stored)
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

        if (Property(root, PathDestinations) is JsonArray rules)
        {
            // Walked in REVERSE so a drop removing an element never shifts an index a later site
            // closed over.
            for (int i = rules.Count - 1; i >= 0; i--)
            {
                if (rules[i] is not JsonObject rule
                    || PropertyName(rule, PathRuleDest) is not { } member
                    || StringOf(rule[member]) is not { } stored)
                {
                    continue;
                }

                int index = i;
                sites.Add(new DestinationSite(
                    $"{PathDestinations}[{index}]",
                    stored,
                    (r, t) => rule[member] = Pair(r, t),
                    () => rules.RemoveAt(index)));
            }
        }

        if (PropertyName(root, UnorganizedDestination) is { } unorganizedKey
            && StringOf(root[unorganizedKey]) is { } unorganized)
        {
            sites.Add(new DestinationSite(
                UnorganizedDestination,
                unorganized,
                (r, t) => root[unorganizedKey] = Pair(r, t),
                () => root.Remove(unorganizedKey),
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

    /// <summary>The node's string value, or <c>null</c> when it is absent or of another kind.</summary>
    private static string? StringOf(JsonNode? node)
        => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static void ConvertGroup(
        JsonObject root,
        string group,
        Dictionary<string, (int Id, IReadOnlyList<int> AlsoMatched)> lookup,
        List<string> dropped,
        List<CaseCollapse> collapses)
    {
        if (Property(root, group) is not JsonObject node)
        {
            return;
        }

        ConvertNameList(node, LegacyWhitelist, WhitelistIds, lookup, dropped, collapses);
        ConvertNameList(node, LegacyBlacklist, BlacklistIds, lookup, dropped, collapses);
    }

    /// <summary>
    /// Replaces <paramref name="from"/>'s name array with an <paramref name="to"/> id array, leaving
    /// both keys absent when the source key is absent.
    /// </summary>
    /// <remarks>
    /// The legacy key is REMOVED rather than left beside its replacement. Two keys carrying the same
    /// rule in different vocabularies is a state nothing in the model can express, so a later read
    /// would have to pick one and the two would drift.
    /// </remarks>
    private static void ConvertNameList(
        JsonObject owner,
        string from,
        string to,
        Dictionary<string, (int Id, IReadOnlyList<int> AlsoMatched)> lookup,
        List<string> dropped,
        List<CaseCollapse> collapses)
    {
        if (PropertyName(owner, from) is not { } storedKey || owner[storedKey] is not JsonArray names)
        {
            return;
        }

        var ids = new JsonArray();
        var seen = new HashSet<int>();
        foreach (var name in StringsOf(names))
        {
            if (!lookup.TryGetValue(name, out var hit))
            {
                dropped.Add(name);
                continue;
            }

            RecordCollapse(collapses, name, hit);
            if (seen.Add(hit.Id))
            {
                ids.Add(hit.Id);
            }
        }

        owner.Remove(storedKey);
        owner[to] = ids;
    }

    /// <summary>Rewrites the tag-destination map's keys from names to ids, in stored order.</summary>
    /// <remarks>
    /// Stored order decides which destination survives when two keys resolve to one id, because the
    /// resolver itself takes the first matching rule. Reversing that here would hand the user a
    /// different destination than the one the pre-conversion resolver would have chosen for the same
    /// blob.
    /// </remarks>
    private static void ConvertDestinationKeys(
        JsonObject root,
        Dictionary<string, (int Id, IReadOnlyList<int> AlsoMatched)> lookup,
        List<string> dropped,
        List<CaseCollapse> collapses,
        List<DiscardedDestination> discarded)
    {
        if (PropertyName(root, TagDestinations) is not { } storedKey
            || root[storedKey] is not JsonObject map)
        {
            return;
        }

        var converted = new JsonObject();
        var claimedBy = new Dictionary<int, string>();

        foreach (var entry in map.ToList())
        {
            // A key that is already an id belongs to a blob this conversion has partly seen before:
            // one half converted and the write interrupted, or a hand-edit. Treating it as a name
            // would resolve it against the tag TABLE, find nothing, and delete a live rule.
            if (IsId(entry.Key, out int existing))
            {
                Claim(existing, entry.Key);
                continue;
            }

            if (!lookup.TryGetValue(entry.Key, out var hit))
            {
                dropped.Add(entry.Key);
                continue;
            }

            RecordCollapse(collapses, entry.Key, hit);
            Claim(hit.Id, entry.Key);

            void Claim(int id, string key)
            {
                if (claimedBy.TryGetValue(id, out var winner))
                {
                    discarded.Add(new DiscardedDestination(key, id, winner));
                    return;
                }

                claimedBy[id] = key;
                converted[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    map[key]?.DeepClone();
            }
        }

        root.Remove(storedKey);
        root[TagDestinations] = converted;
    }

    private static void RecordCollapse(
        List<CaseCollapse> collapses,
        string name,
        (int Id, IReadOnlyList<int> AlsoMatched) hit)
    {
        if (hit.AlsoMatched.Count > 0)
        {
            collapses.Add(new CaseCollapse(name, hit.Id, hit.AlsoMatched));
        }
    }

    /// <summary>
    /// Groups <paramref name="rows"/> by name case-insensitively, keeping the lowest id as the match.
    /// </summary>
    /// <remarks>
    /// Lowest id, not first row, because the row order a database returns is not defined unless it was
    /// ordered - so any other choice would make the conversion's outcome depend on the query plan, and
    /// two installs with the same data could resolve one rule to different entities.
    /// </remarks>
    private static Dictionary<string, (int Id, IReadOnlyList<int> AlsoMatched)> BuildLookup(
        IReadOnlyList<(int Id, string Name)> rows)
    {
        var lookup = new Dictionary<string, (int, IReadOnlyList<int>)>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ids = group.Select(r => r.Id).OrderBy(id => id).ToList();
            lookup[group.Key] = (ids[0], ids.Skip(1).ToList());
        }

        return lookup;
    }

    /// <summary>Whether <paramref name="key"/> is already an id rather than a name.</summary>
    private static bool IsId(string key) => IsId(key, out _);

    private static bool IsId(string key, out int id)
        => int.TryParse(key, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out id);

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

    /// <summary>
    /// The blob's actual spelling of <paramref name="name"/>, or <c>null</c> when it carries no such
    /// property.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively because <see cref="RenamerOptions.JsonOptions"/> binds that way, so a
    /// differently-cased blob is one the model accepts. A case-sensitive lookup here would leave such a
    /// blob unconverted while the stamp still recorded it as done, and its rules would then bind to
    /// nothing with no way back.
    /// </remarks>
    private static string? PropertyName(JsonObject? owner, string name)
    {
        if (owner is null)
        {
            return null;
        }

        foreach (var entry in owner)
        {
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Key;
            }
        }

        return null;
    }

    private static JsonNode? Property(JsonObject? owner, string name)
        => PropertyName(owner, name) is { } key ? owner![key] : null;

    private static JsonObject? Group(JsonObject? root, string name) => Property(root, name) as JsonObject;

    private static IEnumerable<string> NamesIn(JsonObject? owner, string key)
        => Property(owner, key) is JsonArray array ? StringsOf(array) : [];

    private static IEnumerable<string> KeysIn(JsonObject? owner, string key)
        => Property(owner, key) is JsonObject map ? map.Select(e => e.Key) : [];

    private static IEnumerable<string> StringsOf(JsonArray array)
    {
        foreach (var item in array)
        {
            if (item?.GetValueKind() == JsonValueKind.String)
            {
                string value = item.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }
    }

    private static void Collect(List<string> into, IEnumerable<string> names) => into.AddRange(names);

    private static IReadOnlyList<string> Distinct(List<string> names)
        => [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
}
