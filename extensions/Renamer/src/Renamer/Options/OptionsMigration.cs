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

    // Mirrors RenamerOptions.JsonOptions.PropertyNameCaseInsensitive: the stored blob is read back
    // case-insensitively, so a hand-edited "excludetags" is live configuration and must convert too.
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// What a stored blob still holds in the legacy, name-keyed shape — and therefore which entity
    /// tables the conversion has to resolve against.
    /// </summary>
    public readonly record struct LegacyFields(bool Tags, bool Performers)
    {
        /// <summary>True when anything at all still needs converting.</summary>
        public bool Any => Tags || Performers;
    }

    /// <summary>The converted blob plus every stored name that matched no entity and was dropped.</summary>
    public sealed record Conversion(string Json, IReadOnlyList<string> DroppedNames);

    /// <summary>
    /// Reports which halves of <paramref name="json"/> are still name-keyed, without touching a
    /// database. An absent, blank or unparseable blob needs nothing: there is no configuration to lose.
    /// </summary>
    public static LegacyFields Scan(string? json)
    {
        var root = TryParse(json);
        if (root is null)
        {
            return default;
        }

        bool tags = root[LegacyExcludeTags] is JsonArray
            || GroupIsNameKeyed(root, TagsGroup)
            || HasNameKeyedDestinations(root);

        return new LegacyFields(tags, GroupIsNameKeyed(root, PerformersGroup));
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
            return new Conversion(json, []);
        }

        var tagIds = BuildLookup(tags);
        var performerIds = BuildLookup(performers);
        var dropped = new List<string>();

        ConvertNameList(root[TagsGroup] as JsonObject, LegacyWhitelist, WhitelistIds, tagIds, dropped);
        ConvertNameList(root[TagsGroup] as JsonObject, LegacyBlacklist, BlacklistIds, tagIds, dropped);
        ConvertNameList(root[PerformersGroup] as JsonObject, LegacyWhitelist, WhitelistIds, performerIds, dropped);
        ConvertNameList(root[PerformersGroup] as JsonObject, LegacyBlacklist, BlacklistIds, performerIds, dropped);
        ConvertNameList(root, LegacyExcludeTags, ExcludeTagIds, tagIds, dropped);
        ConvertDestinations(root, tagIds, dropped);

        return new Conversion(root.ToJsonString(), dropped);
    }

    private static JsonObject? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json, NodeOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the name → id lookup used by every field.
    /// </summary>
    /// <remarks>
    /// Resolution is case-insensitive because matching a stored rule against a live entity name was
    /// case-insensitive before this migration: a rule stored as <c>"4k"</c> against a tag named
    /// <c>4K</c> was live, and a case-sensitive lookup would silently drop it.
    /// <para>
    /// Where two entities differ only by letter case, they share one lookup entry and two stored rules
    /// collapse onto a single id — the one genuine narrowing this migration carries. Before it, such a
    /// rule matched BOTH rows; afterwards it matches one. The lowest id wins so the collapse is decided
    /// by the data rather than by the order the rows came back in.
    /// </para>
    /// </remarks>
    private static Dictionary<string, int> BuildLookup(IReadOnlyList<(int Id, string Name)> rows)
    {
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in rows)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!lookup.TryGetValue(name, out int existing) || id < existing)
            {
                lookup[name] = id;
            }
        }

        return lookup;
    }

    private static void ConvertNameList(
        JsonObject? owner,
        string legacyName,
        string idName,
        Dictionary<string, int> lookup,
        List<string> dropped)
    {
        if (owner?[legacyName] is not JsonArray legacy)
        {
            return;
        }

        var ids = new List<int>();
        var seen = new HashSet<int>();

        // A half-converted blob carrying both spellings keeps the ids it already had.
        if (owner[idName] is JsonArray existing)
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

            if (!lookup.TryGetValue(name, out int id))
            {
                dropped.Add(name);
                continue;
            }

            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        owner[idName] = new JsonArray([.. ids.Select(id => (JsonNode)JsonValue.Create(id))]);
        owner.Remove(legacyName);
    }

    private static void ConvertDestinations(
        JsonObject root,
        Dictionary<string, int> lookup,
        List<string> dropped)
    {
        if (root[TagDestinations] is not JsonObject legacy)
        {
            return;
        }

        var converted = new JsonObject();
        foreach (var (key, value) in legacy)
        {
            string? idKey = IsIdKey(key) ? key : Resolve(key);
            if (idKey is null || converted.ContainsKey(idKey))
            {
                continue;
            }

            converted[idKey] = value?.DeepClone();
        }

        root[TagDestinations] = converted;

        string? Resolve(string name)
        {
            if (lookup.TryGetValue(name, out int id))
            {
                return id.ToString(CultureInfo.InvariantCulture);
            }

            dropped.Add(name);
            return null;
        }
    }

    private static bool GroupIsNameKeyed(JsonObject root, string group) =>
        root[group] is JsonObject g && (g[LegacyWhitelist] is JsonArray || g[LegacyBlacklist] is JsonArray);

    private static bool HasNameKeyedDestinations(JsonObject root) =>
        root[TagDestinations] is JsonObject map && map.Any(entry => !IsIdKey(entry.Key));

    // A converted map's keys are the invariant decimal spelling of an int, which is also what a
    // fresh install writes before this conversion ever runs — so an int-spelled key is read as an id
    // that is already migrated, never as a tag name. A tag whose name is a bare number therefore
    // cannot carry a destination rule through the conversion; treating it as a name instead would
    // destroy the routing of every install that already stores ids.
    private static bool IsIdKey(string key) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
        && id.ToString(CultureInfo.InvariantCulture) == key;
}
