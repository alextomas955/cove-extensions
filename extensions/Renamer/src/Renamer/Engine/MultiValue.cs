using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Engine;

// Resolves the $performers and $tags lists into one joined string under the field's
// MultiValueOptions. The order is whitelist, blacklist, sort, max count, then join with the
// configured separator.
public static class MultiValue
{
    // Values carrying no entity identity: sort, then max, then join. The whitelist and blacklist are
    // skipped because this overload serves the fixed preview samples, whose values name no library
    // entity, so matching them against a rule that identifies a real tag could only agree by chance.
    public static string Resolve(IReadOnlyList<string> values, MultiValueOptions m)
    {
        IEnumerable<string> seq = values;

        if (m.Sort == SortOrder.NameAsc)
        {
            seq = seq.OrderBy(v => v, StringComparer.OrdinalIgnoreCase);
        }

        var list = seq.ToList();

        if (m.MaxCount > 0 && list.Count > m.MaxCount)
        {
            // DropAll empties the whole field, which makes the token empty and so collapses any {}
            // group around it.
            list = m.OnOverflow == OverflowPolicy.KeepFirst
                ? list.Take(m.MaxCount).ToList()
                : new List<string>();
        }

        return string.Join(m.Separator, list);
    }

    // The whitelist and blacklist match on each tag's stable id, and the surviving tags render their
    // current names. Sorting is by name, which is what the token renders.
    public static string Resolve(IReadOnlyList<(int Id, string Name)> tags, MultiValueOptions m)
    {
        IEnumerable<(int Id, string Name)> seq = tags;

        if (m.WhitelistIds.Count > 0)
        {
            seq = seq.Where(t => m.WhitelistIds.Contains(t.Id));
        }

        if (m.BlacklistIds.Count > 0)
        {
            seq = seq.Where(t => !m.BlacklistIds.Contains(t.Id));
        }

        if (m.Sort == SortOrder.NameAsc)
        {
            seq = seq.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
        }

        var list = seq.ToList();

        if (m.MaxCount > 0 && list.Count > m.MaxCount)
        {
            list = m.OnOverflow == OverflowPolicy.KeepFirst
                ? list.Take(m.MaxCount).ToList()
                : [];
        }

        return string.Join(m.Separator, list.Select(t => t.Name));
    }

    // Performer records also carry id, favorite and gender. The order is whitelist, blacklist by id,
    // gender-ignore, sort, gender-order, max count, then join of the names. Gender-ignore and
    // gender-order run before the max count, so they change which performers survive the limit.
    public static string Resolve(IReadOnlyList<RenamerPerformer> performers, MultiValueOptions m)
    {
        IEnumerable<RenamerPerformer> seq = performers;

        if (m.WhitelistIds.Count > 0)
        {
            seq = seq.Where(p => m.WhitelistIds.Contains(p.Id));
        }

        if (m.BlacklistIds.Count > 0)
        {
            seq = seq.Where(p => !m.BlacklistIds.Contains(p.Id));
        }

        // Ignored genders drop before the limit, so an ignored gender frees an overflow slot. A
        // performer with no gender set is kept.
        if (m.IgnoreGenders.Count > 0)
        {
            seq = seq.Where(p => p.Gender is null
                || !m.IgnoreGenders.Contains(p.Gender, StringComparer.OrdinalIgnoreCase));
        }

        seq = m.Sort switch
        {
            SortOrder.NameAsc => seq.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            SortOrder.IdAsc => seq.OrderBy(p => p.Id),
            SortOrder.FavoriteFirst => seq.OrderByDescending(p => p.Favorite)
                                          .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => seq,
        };

        // A stable ordering layered over the chosen sort, so performers of the same gender keep it.
        if (m.GenderOrder.Count > 0)
        {
            seq = seq.OrderBy(p => GenderRank(p.Gender, m.GenderOrder));
        }

        var list = seq.ToList();

        if (m.MaxCount > 0 && list.Count > m.MaxCount)
        {
            list = m.OnOverflow == OverflowPolicy.KeepFirst
                ? list.Take(m.MaxCount).ToList()
                : new List<RenamerPerformer>();
        }

        return string.Join(m.Separator, list.Select(p => p.Name));
    }

    // A gender absent from the list, null included, ranks after every listed gender.
    private static int GenderRank(string? gender, List<string> order)
    {
        if (gender is null)
        {
            return order.Count;
        }

        int idx = order.FindIndex(g => string.Equals(g, gender, StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? order.Count : idx;
    }
}
